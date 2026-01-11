using System;

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

	private int _currentTick;
	private int _lastAcknowledgedTick;
	private int _lastProcessedInputTick; // Server-side: last input tick we processed
	private Vector3 _reconciliationOffset;
	private IPredicted _predicted;
	private PredictionInput? _previousInput; // For redundant sending

	// Current input being built this frame
	private PredictionInput _pendingInput;

	/// <summary>
	/// Returns true if the local client is the controller of this predicted object.
	/// </summary>
	public bool IsLocalController => ControllerId != Guid.Empty && ControllerId == Connection.Local?.Id;

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
	}

	protected override void OnAwake()
	{
		Network.Flags |= NetworkFlags.NoTransformSync;
	}

	protected override void OnStart()
	{
		// Find the IPredicted implementation on this GameObject
		_predicted = Components.Get<IPredicted>( FindMode.EverythingInSelf );

		if ( _predicted == null )
		{
			Log.Warning( $"PredictionController on {GameObject.Name} requires an IPredicted component!" );
		}
	}

	protected override void OnUpdate()
	{
		if ( _predicted == null )
			return;

		if ( IsLocalController )
		{
			UpdateLocalPlayer();
		}
		else
		{
			UpdateRemotePlayer();
		}
	}

	/// <summary>
	/// Call this to set the current frame's input before simulation.
	/// Typically called from your player controller's Update.
	/// </summary>
	public void SetInput( PredictionInput input )
	{
		_pendingInput = input;
		_pendingInput.Tick = _currentTick;
	}

	/// <summary>
	/// Build input from common sources. Call this or SetInput each frame.
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

	private void UpdateLocalPlayer()
	{
		// Apply any smoothing from reconciliation
		if ( SmoothReconciliation && _reconciliationOffset.LengthSquared > 0.0001f )
		{
			_reconciliationOffset = Vector3.Lerp( _reconciliationOffset, Vector3.Zero, Time.Delta * ReconciliationSpeed );
		}
		else
		{
			_reconciliationOffset = Vector3.Zero;
		}

		// Simulate with current input
		_predicted.OnSimulate( _pendingInput );

		// Store input and state for potential reconciliation, sends to server
		StoreHistory( _pendingInput );

		_currentTick++;

		// Apply visual offset for smooth reconciliation
		// Note: This affects rendering only, not simulation
	}

	private void UpdateRemotePlayer()
	{
		if ( !InterpolateRemote || _remoteStateBuffer.Count < 2 )
			return;

		// Render in the past to have states to interpolate between
		var renderTime = Time.Now - InterpolationDelay;

		// Find two states that bracket our render time
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

		// If render time is beyond our buffer, use the latest state
		if ( renderTime > states[^1].Time )
		{
			WorldPosition = states[^1].Position;
			WorldRotation = states[^1].Rotation;
			return;
		}

		// If render time is before our buffer, use the earliest state
		if ( renderTime < states[0].Time )
		{
			WorldPosition = states[0].Position;
			WorldRotation = states[0].Rotation;
			return;
		}

		// Interpolate between the two bracketing states
		var duration = to.Time - from.Time;
		var t = duration > 0 ? (renderTime - from.Time) / duration : 0f;
		t = t.Clamp( 0f, 1f );

		WorldPosition = Vector3.Lerp( from.Position, to.Position, t );
		WorldRotation = Rotation.Lerp( from.Rotation, to.Rotation, t );
	}

	private void StoreHistory( PredictionInput input )
	{
		// Store input
		_inputHistory.Enqueue( input );
		while ( _inputHistory.Count > HistorySize )
			_inputHistory.Dequeue();

		// Store resulting state
		var state = CaptureState( input.Tick );
		_stateHistory.Enqueue( state );
		while ( _stateHistory.Count > HistorySize )
			_stateHistory.Dequeue();

		// Send input to the server with previous input for redundancy
		SendInputToServer( input, _previousInput );
		_previousInput = input;
	}

	private PredictionState CaptureState( int tick )
	{
		return new PredictionState
		{
			Tick = tick,
			Time = Time.Now,
			Position = WorldPosition,
			Rotation = WorldRotation,
			Velocity = Components.Get<Rigidbody>()?.Velocity ?? Vector3.Zero
		};
	}

	private void ApplyState( PredictionState state )
	{
		WorldPosition = state.Position;
		WorldRotation = state.Rotation;

		var rb = Components.Get<Rigidbody>();
		if ( rb != null )
		{
			rb.Velocity = state.Velocity;
		}
	}

	[Rpc.Host( NetFlags.Unreliable )]
	private void SendInputToServer( PredictionInput input, PredictionInput? previousInput )
	{
		if ( !Networking.IsHost )
			return;

		// Ignore old inputs (out of order packet)
		if ( input.Tick <= _lastProcessedInputTick )
			return;

		// If we missed an input and have the previous one, process it first
		if ( previousInput.HasValue && previousInput.Value.Tick > _lastProcessedInputTick )
		{
			_predicted.OnSimulate( previousInput.Value );
			_lastProcessedInputTick = previousInput.Value.Tick;
		}

		// Process current input
		_predicted.OnSimulate( input );
		_lastProcessedInputTick = input.Tick;

		// Capture authoritative state
		var serverState = CaptureState( input.Tick );

		// Send correction back to the client
		using ( Rpc.FilterInclude( Rpc.Caller ) )
		{
			ReceiveServerState( serverState );
		}
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	private void ReceiveServerState( PredictionState serverState )
	{
		if ( !IsLocalController )
		{
			// For remote players, buffer for interpolation (ignore out of order)
			if ( _remoteStateBuffer.Count > 0 )
			{
				var latest = _remoteStateBuffer.ToArray()[^1];
				if ( serverState.Tick <= latest.Tick )
					return;
			}

			_remoteStateBuffer.Enqueue( serverState );
			while ( _remoteStateBuffer.Count > HistorySize )
				_remoteStateBuffer.Dequeue();
			return;
		}

		// Ignore old states (out of order packet)
		if ( serverState.Tick <= _lastAcknowledgedTick )
			return;

		// Find our predicted state for this tick
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

		// Check if reconciliation is needed
		if ( predictedState.Value.Equals( serverState, ReconciliationTolerance ) )
			return;

		// Misprediction detected - reconcile!
		Reconcile( serverState );
	}

	private void Reconcile( PredictionState serverState )
	{
		// Store the current position for smooth interpolation
		var previousPosition = WorldPosition;

		// Rewind to the server state
		ApplyState( serverState );

		// Re-simulate all inputs after the corrected tick
		var inputsToReplay = new List<PredictionInput>();
		foreach ( var input in _inputHistory )
		{
			if ( input.Tick > serverState.Tick )
				inputsToReplay.Add( input );
		}

		foreach ( var input in inputsToReplay )
		{
			_predicted.OnSimulate( input );
		}

		// Calculate offset for smooth visual correction
		if ( SmoothReconciliation )
		{
			_reconciliationOffset = previousPosition - WorldPosition;
		}

		// Notify the predicted component
		_predicted.OnReconcile();

		// Clear old history
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
	/// Use this for rendering if you need the smoothed position.
	/// </summary>
	public Vector3 GetVisualPosition()
	{
		return WorldPosition + _reconciliationOffset;
	}

	/// <summary>
	/// Get the current prediction tick.
	/// </summary>
	public int CurrentTick => _currentTick;

	/// <summary>
	/// Get the last tick acknowledged by the server.
	/// </summary>
	public int LastAcknowledgedTick => _lastAcknowledgedTick;

	/// <summary>
	/// How many ticks ahead of the server we are (input latency).
	/// </summary>
	public int TicksAhead => _currentTick - _lastAcknowledgedTick;
}
