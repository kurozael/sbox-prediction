using System;
using Sandbox;

namespace Prediction;

/// <summary>
/// Add this component to any GameObject that needs client-side prediction.
/// Works with PredictionSystem for centralized tick management.
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
	/// How much of the correction to apply per reconciliation (0-1).
	/// Lower values = smoother but slower corrections.
	/// </summary>
	[Property, Range( 0.1f, 1f )]
	public float CorrectionBlend { get; set; } = 0.5f;

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

	// Input/state history
	private readonly Queue<PredictionInput> _inputHistory = new();
	private readonly Queue<PredictionState> _stateHistory = new();
	private readonly Queue<PredictionState> _remoteStateBuffer = new();

	// Server-side input queue
	private readonly Queue<PredictionInput> _serverInputQueue = new();
	private Connection _controllerConnection;
	private int _serverTick;
	private PredictionInput _lastServerInput;

	private int _lastQueuedInputTick;
	private IPredicted _predicted;
	private PredictionInput? _previousInput;
	private PredictionInput _pendingInput;

	private PredictionSystem _system;

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
		GameObject.Flags |= GameObjectFlags.NoInterpolation;
		Network.Flags |= NetworkFlags.NoTransformSync;
		Network.Flags |= NetworkFlags.NoInterpolation;
	}

	protected override void OnStart()
	{
		_predicted = Components.Get<IPredicted>( FindMode.EverythingInSelf );

		if ( _predicted == null )
		{
			Log.Warning( $"PredictionController on {GameObject.Name} requires an IPredicted component!" );
		}

		// Register with the prediction system
		_system = Scene.GetSystem<PredictionSystem>();
		_system?.Register( this );
	}

	protected override void OnDestroy()
	{
		_system?.Unregister( this );
	}

	/// <summary>
	/// Called by PredictionSystem to process server input queue (host only, remote controllers).
	/// </summary>
	public void ProcessServerInputQueue()
	{
		if ( _predicted == null )
			return;

		const int maxInputsPerFrame = 5;
		var processed = 0;

		while ( _serverInputQueue.Count > 0 && processed < maxInputsPerFrame )
		{
			var input = _serverInputQueue.Peek();

			// Fill gaps with the last known input
			while ( _serverTick < input.Tick && processed < maxInputsPerFrame )
			{
				SimulateInternal( _lastServerInput );
				_serverTick++;
				processed++;
			}

			if ( processed >= maxInputsPerFrame )
				break;

			_serverInputQueue.Dequeue();
			processed++;

			SimulateInternal( input );
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

			using ( Rpc.FilterExclude( _controllerConnection ) )
			{
				BroadcastStateToClients( serverState );
			}
		}
	}

	/// <summary>
	/// Called by PredictionSystem to simulate (local controllers only).
	/// </summary>
	public void Simulate()
	{
		if ( _predicted == null )
			return;

		var currentTick = _system?.CurrentTick ?? 0;

		if ( IsHostController )
		{
			SimulateHostController( currentTick );
		}
		else
		{
			SimulateClientController( currentTick );
		}
	}

	/// <summary>
	/// Called by PredictionSystem every frame to update visuals.
	/// </summary>
	public void UpdateVisuals()
	{
		if ( !IsLocalController )
		{
			UpdateRemotePlayerInterpolation();
		}
	}

	private void SimulateHostController( int currentTick )
	{
		_pendingInput.Tick = currentTick;
		_predicted.BuildInput( ref _pendingInput );

		SimulateInternal( _pendingInput );

		var state = CaptureState( currentTick );
		BroadcastStateToClients( state );
	}

	private void SimulateClientController( int currentTick )
	{
		_pendingInput.Tick = currentTick;
		_predicted.BuildInput( ref _pendingInput );

		SimulateInternal( _pendingInput );

		StoreHistory( _pendingInput );

		SendInputToServer( _pendingInput, _previousInput );
		_previousInput = _pendingInput;
	}

	private void SimulateInternal( PredictionInput input )
	{
		using ( Time.Scope( Time.Now, _system?.FixedDelta ?? Scene.FixedDelta ) )
		{
			_predicted.OnSimulate( input );
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

	/// <summary>
	/// Blend current state toward target state by the given amount.
	/// </summary>
	private void BlendTowardState( PredictionState target, float blend )
	{
		WorldPosition = Vector3.Lerp( WorldPosition, target.Position, blend );
		WorldRotation = Rotation.Lerp( WorldRotation, target.Rotation, blend );

		// Blend velocity too
		var currentState = new PredictionState();
		_predicted?.CaptureState( ref currentState );

		var blendedState = target with
		{
			Position = WorldPosition,
			Rotation = WorldRotation,
			Velocity = Vector3.Lerp( currentState.Velocity, target.Velocity, blend )
		};

		_predicted?.ApplyState( blendedState );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	private void BroadcastStateToClients( PredictionState state )
	{
		if ( Networking.IsHost )
			return;

		// Update server tick tracking
		_system?.UpdateServerTick( state.Tick );

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

		// Find our predicted state for this tick
		PredictionState? predictedState = null;
		foreach ( var state in _stateHistory )
		{
			if ( state.Tick != serverState.Tick )
				continue;

			predictedState = state;
			break;
		}

		if ( !predictedState.HasValue )
			return;

		// Track acknowledged tick
		_system?.AcknowledgeTick( serverState.Tick );

		// Check if prediction matches server
		if ( predictedState.Value.Equals( serverState, ReconciliationTolerance ) )
		{
			// Prediction was correct, just clean up old history
			ClearHistoryBefore( serverState.Tick );
			return;
		}

		// Prediction was wrong, need to reconcile
		Reconcile( serverState, predictedState.Value );
	}

	private void Reconcile( PredictionState serverState, PredictionState predictedState )
	{
		// Instead of snapping to the server state, blend toward it
		// This creates a "soft" correction that reduces jitter

		// Calculate how wrong we were
		var positionError = (serverState.Position - predictedState.Position).Length;

		// For large errors, use full correction. For small errors, blend gently.
		var blend = CorrectionBlend;
		if ( positionError < 0.5f )
		{
			// Small error - use gentler correction
			blend *= 0.5f;
		}

		// Blend the server state into our current predicted state at that tick
		// Then replay inputs from there
		BlendTowardState( serverState, blend );

		// Replay all inputs after the server state
		var inputsToReplay = new List<PredictionInput>();
		foreach ( var input in _inputHistory )
		{
			if ( input.Tick > serverState.Tick )
				inputsToReplay.Add( input );
		}

		foreach ( var input in inputsToReplay )
		{
			SimulateInternal( input );
		}

		_predicted?.OnReconcile();

		ClearHistoryBefore( serverState.Tick );
	}

	private void ClearHistoryBefore( int tick )
	{
		while ( _inputHistory.Count > 0 && _inputHistory.Peek().Tick <= tick )
			_inputHistory.Dequeue();

		while ( _stateHistory.Count > 0 && _stateHistory.Peek().Tick <= tick )
			_stateHistory.Dequeue();
	}
}
