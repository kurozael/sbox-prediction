using System;
using Sandbox;

namespace Prediction;

/// <summary>
/// Add this component to any GameObject that needs client-side prediction.
/// Requires the GameObject to have NetworkFlags.NoTransformSync set.
/// </summary>
public sealed class PredictionController : Component
{
	/// <summary>
	/// How many ticks of input/state history to keep for reconciliation.
	/// </summary>
	[Property]
	public int HistorySize { get; set; } = 128;

	/// <summary>
	/// Tolerance for position mismatch before triggering reconciliation.
	/// </summary>
	[Property]
	public float ReconciliationTolerance { get; set; } = 0.1f;

	/// <summary>
	/// Enable smooth interpolation to hide reconciliation snaps.
	/// </summary>
	[Property]
	public bool SmoothReconciliation { get; set; } = true;

	/// <summary>
	/// How fast to interpolate after reconciliation (higher = faster correction).
	/// </summary>
	[Property]
	public float ReconciliationSpeed { get; set; } = 10f;

	/// <summary>
	/// Enable interpolation for remote players (non-predicted entities).
	/// </summary>
	[Property]
	public bool InterpolateRemote { get; set; } = true;

	/// <summary>
	/// Interpolation delay in seconds for remote entities.
	/// </summary>
	[Property]
	public float InterpolationDelay { get; set; } = 0.1f;

	/// <summary>
	/// The Connection Id of the player who controls this predicted object.
	/// Set via SetController() - synced from host to all clients.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	private Guid ControllerId { get; set; }

	// Internal state
	private readonly Queue<PredictionInput> _inputHistory = new();
	private readonly Queue<PredictionState> _stateHistory = new();
	private readonly Queue<PredictionState> _remoteStateBuffer = new();

	// Server-side input queue
	private readonly Queue<PredictionInput> _serverInputQueue = new();
	private Connection _controllerConnection;
	private int _serverTick;
	private PredictionInput _lastServerInput;

	private int _currentTick;
	private int _lastAcknowledgedTick;
	private int _lastQueuedInputTick;
	private Vector3 _reconciliationOffset;
	private IPredicted _predicted;
	private PredictionInput? _previousInput;
	private PredictionInput _pendingInput;

	/// <summary>
	/// Fixed timestep for simulation.
	/// </summary>
	private float FixedDelta => Scene.FixedDelta;

	/// <summary>
	/// Returns true if the local client is the controller of this predicted object.
	/// </summary>
	public bool IsLocalController => ControllerId != Guid.Empty && ControllerId == Connection.Local?.Id;

	/// <summary>
	/// Returns true if we are the host AND the controller (no prediction needed, we are authoritative).
	/// </summary>
	private bool IsHostController => Networking.IsHost && IsLocalController;

	/// <summary>
	/// Set the controller of this predicted object. Call this on the host when spawning.
	/// </summary>
	public void SetController( Connection controller )
	{
		if ( !Networking.IsHost )
		{
			Log.Warning( "SetController can only be called on the host!" );
			return;
		}

		ControllerId = controller.Id;
		_controllerConnection = controller;
	}

	/// <summary>
	/// Set the controller by Connection Id directly.
	/// </summary>
	public void SetController( Guid connectionId )
	{
		if ( !Networking.IsHost )
		{
			Log.Warning( "SetController can only be called on the host!" );
			return;
		}

		ControllerId = connectionId;
		_controllerConnection = Connection.All.FirstOrDefault( c => c.Id == connectionId );
	}

	protected override void OnAwake()
	{
		Network.Flags |= NetworkFlags.NoTransformSync;
	}

	protected override void OnStart()
	{
		_predicted = Components.Get<IPredicted>( FindMode.EverythingInSelf );

		if ( _predicted == null )
		{
			Log.Warning( $"PredictionController on {GameObject.Name} requires an IPredicted component!" );
		}
	}

	protected override void OnUpdate()
	{
		// Reconciliation smoothing runs every frame
		if ( IsLocalController && !IsHostController )
		{
			if ( SmoothReconciliation && _reconciliationOffset.LengthSquared > 0.0001f )
			{
				_reconciliationOffset = Vector3.Lerp( _reconciliationOffset, Vector3.Zero, Time.Delta * ReconciliationSpeed );
			}
			else
			{
				_reconciliationOffset = Vector3.Zero;
			}
		}

		// Remote player interpolation
		if ( !IsLocalController )
		{
			UpdateRemotePlayerInterpolation();
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( _predicted == null )
			return;

		// Host processes queued inputs from remote controllers
		if ( Networking.IsHost && !IsLocalController )
		{
			ProcessServerInputQueue();
		}

		// Local controller simulates every fixed update
		if ( IsLocalController )
		{
			if ( IsHostController )
			{
				SimulateHostController();
			}
			else
			{
				SimulateClientController();
			}
		}
	}

	/// <summary>
	/// Call this to set the current frame's input before simulation.
	/// </summary>
	public void SetInput( PredictionInput input )
	{
		_pendingInput = input;
		_pendingInput.Tick = _currentTick;
	}

	/// <summary>
	/// Build input from common sources.
	/// </summary>
	public void BuildInput()
	{
		_pendingInput = new PredictionInput
		{
			Tick = _currentTick,
			MoveDirection = Input.AnalogMove,
			ViewAngles = Input.AnalogLook,
			Jump = Input.Down( "jump" ),
			Attack = Input.Down( "attack1" ),
			Use = Input.Down( "use" )
		};
	}

	private void SimulateHostController()
	{
		using ( Time.Scope( Time.Now, FixedDelta ) )
		{
			_predicted.OnSimulate( _pendingInput );
		}

		var state = CaptureState( _currentTick );
		BroadcastStateToClients( state );

		_currentTick++;
	}

	private void SimulateClientController()
	{
		using ( Time.Scope( Time.Now, FixedDelta ) )
		{
			_predicted.OnSimulate( _pendingInput );
		}

		StoreHistory( _pendingInput );

		SendInputToServer( _pendingInput, _previousInput );
		_previousInput = _pendingInput;

		_currentTick++;
	}

	private void ProcessServerInputQueue()
	{
		const int maxInputsPerFrame = 5;
		var processed = 0;

		while ( _serverInputQueue.Count > 0 && processed < maxInputsPerFrame )
		{
			var input = _serverInputQueue.Peek();

			// Fill gaps with the last known input
			while ( _serverTick < input.Tick && processed < maxInputsPerFrame )
			{
				using ( Time.Scope( Time.Now, FixedDelta ) )
				{
					_predicted.OnSimulate( _lastServerInput );
				}

				_serverTick++;
				processed++;
			}

			if ( processed >= maxInputsPerFrame )
				break;

			_serverInputQueue.Dequeue();
			processed++;

			using ( Time.Scope( Time.Now, FixedDelta ) )
			{
				_predicted.OnSimulate( input );
			}

			_lastServerInput = input;
			_serverTick++;

			var serverState = CaptureState( input.Tick );

			if ( _controllerConnection != null )
			{
				using ( Rpc.FilterInclude( _controllerConnection ) )
				{
					SendServerStateToController( serverState );
				}
			}

			BroadcastStateToClients( serverState );
		}
	}

	private void UpdateRemotePlayerInterpolation()
	{
		if ( !InterpolateRemote || _remoteStateBuffer.Count < 2 )
			return;

		var renderTime = Time.Now - InterpolationDelay;
		var states = _remoteStateBuffer.ToArray();
		var from = states[0];
		var to = states[0];

		for ( var i = 0; i < states.Length - 1; i++ )
		{
			if ( states[i].Time <= renderTime && states[i + 1].Time >= renderTime )
			{
				from = states[i];
				to = states[i + 1];
				break;
			}
		}

		if ( renderTime > states[^1].Time )
		{
			WorldPosition = states[^1].Position;
			WorldRotation = states[^1].Rotation;
			return;
		}

		if ( renderTime < states[0].Time )
		{
			WorldPosition = states[0].Position;
			WorldRotation = states[0].Rotation;
			return;
		}

		var duration = to.Time - from.Time;
		var t = duration > 0 ? (renderTime - from.Time) / duration : 0f;
		t = t.Clamp( 0f, 1f );

		WorldPosition = Vector3.Lerp( from.Position, to.Position, t );
		WorldRotation = Rotation.Lerp( from.Rotation, to.Rotation, t );
	}

	private void StoreHistory( PredictionInput input )
	{
		_inputHistory.Enqueue( input );
		while ( _inputHistory.Count > HistorySize )
			_inputHistory.Dequeue();

		var state = CaptureState( input.Tick );
		_stateHistory.Enqueue( state );
		while ( _stateHistory.Count > HistorySize )
			_stateHistory.Dequeue();
	}

	private PredictionState CaptureState( int tick )
	{
		var state = new PredictionState
		{
			Tick = tick,
			Time = Time.Now,
			Position = WorldPosition,
			Rotation = WorldRotation
		};

		_predicted?.CaptureState( ref state );

		return state;
	}

	private void ApplyState( PredictionState state )
	{
		WorldPosition = state.Position;
		WorldRotation = state.Rotation;

		_predicted?.ApplyState( state );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	private void BroadcastStateToClients( PredictionState state )
	{
		if ( Networking.IsHost )
			return;

		if ( _remoteStateBuffer.Count > 0 )
		{
			var latest = _remoteStateBuffer.ToArray()[^1];
			if ( state.Tick <= latest.Tick )
				return;
		}

		_remoteStateBuffer.Enqueue( state );
		while ( _remoteStateBuffer.Count > HistorySize )
			_remoteStateBuffer.Dequeue();
	}

	[Rpc.Host( NetFlags.Unreliable )]
	private void SendInputToServer( PredictionInput input, PredictionInput? previousInput )
	{
		if ( !Networking.IsHost )
			return;

		if ( _serverTick == 0 && _lastQueuedInputTick == 0 )
		{
			_serverTick = input.Tick;
		}

		if ( previousInput.HasValue && previousInput.Value.Tick > _lastQueuedInputTick )
		{
			_serverInputQueue.Enqueue( previousInput.Value );
			_lastQueuedInputTick = previousInput.Value.Tick;
		}

		if ( input.Tick <= _lastQueuedInputTick )
			return;

		_serverInputQueue.Enqueue( input );
		_lastQueuedInputTick = input.Tick;

		while ( _serverInputQueue.Count > HistorySize )
			_serverInputQueue.Dequeue();
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	private void SendServerStateToController( PredictionState serverState )
	{
		if ( !IsLocalController || Networking.IsHost )
			return;

		if ( serverState.Tick <= _lastAcknowledgedTick )
			return;

		PredictionState? predictedState = null;
		foreach ( var state in _stateHistory )
		{
			if ( state.Tick == serverState.Tick )
			{
				predictedState = state;
				break;
			}
		}

		if ( !predictedState.HasValue )
			return;

		_lastAcknowledgedTick = serverState.Tick;

		if ( predictedState.Value.Equals( serverState, ReconciliationTolerance ) )
			return;

		Reconcile( serverState );
	}

	private void Reconcile( PredictionState serverState )
	{
		var previousPosition = WorldPosition;

		ApplyState( serverState );

		var inputsToReplay = new List<PredictionInput>();
		foreach ( var input in _inputHistory )
		{
			if ( input.Tick > serverState.Tick )
				inputsToReplay.Add( input );
		}

		foreach ( var input in inputsToReplay )
		{
			using ( Time.Scope( Time.Now, FixedDelta ) )
			{
				_predicted.OnSimulate( input );
			}
		}

		if ( SmoothReconciliation )
		{
			_reconciliationOffset = previousPosition - WorldPosition;
		}

		_predicted.OnReconcile();

		ClearHistoryBefore( serverState.Tick );
	}

	private void ClearHistoryBefore( int tick )
	{
		while ( _inputHistory.Count > 0 && _inputHistory.Peek().Tick <= tick )
			_inputHistory.Dequeue();

		while ( _stateHistory.Count > 0 && _stateHistory.Peek().Tick <= tick )
			_stateHistory.Dequeue();
	}

	/// <summary>
	/// Get the visual position including reconciliation smoothing offset.
	/// </summary>
	public Vector3 GetVisualPosition()
	{
		return WorldPosition + _reconciliationOffset;
	}

	public int CurrentTick => _currentTick;
	public int LastAcknowledgedTick => _lastAcknowledgedTick;
	public int TicksAhead => _currentTick - _lastAcknowledgedTick;
}
