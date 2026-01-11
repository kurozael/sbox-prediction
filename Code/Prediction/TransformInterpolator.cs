namespace Prediction;

/// <summary>
/// Handles smooth interpolation for remote (non-predicted) entities.
/// Add this to entities that other players control but you observe.
/// </summary>
public sealed class TransformInterpolator : Component
{
	/// <summary>
	/// How far in the past to render (in seconds).
	/// Higher values = smoother but more delay.
	/// </summary>
	[Property]
	public float InterpolationDelay { get; set; } = 0.1f;

	/// <summary>
	/// Maximum buffer size for state snapshots.
	/// </summary>
	[Property]
	public int BufferSize { get; set; } = 32;

	/// <summary>
	/// If we fall too far behind, teleport instead of interpolating.
	/// </summary>
	[Property]
	public float TeleportThreshold { get; set; } = 5f;

	private readonly struct TransformSnapshot
	{
		public readonly float Time;
		public readonly Vector3 Position;
		public readonly Rotation Rotation;

		public TransformSnapshot( float time, Vector3 position, Rotation rotation )
		{
			Time = time;
			Position = position;
			Rotation = rotation;
		}
	}

	private readonly List<TransformSnapshot> _buffer = new();
	private Vector3 _targetPosition;
	private Rotation _targetRotation;
	private bool _initialized;

	protected override void OnUpdate()
	{
		// Only interpolate for remote entities
		if ( Network.IsOwner )
			return;

		if ( !_initialized )
		{
			_targetPosition = WorldPosition;
			_targetRotation = WorldRotation;
			_initialized = true;
		}

		Interpolate();
	}

	/// <summary>
	/// Call this when receiving a new authoritative state from the server.
	/// </summary>
	public void AddSnapshot( Vector3 position, Rotation rotation )
	{
		var snapshot = new TransformSnapshot( Time.Now, position, rotation );

		// Insert in order (should usually be at the end)
		_buffer.Add( snapshot );

		// Trim old entries
		while ( _buffer.Count > BufferSize )
		{
			_buffer.RemoveAt( 0 );
		}
	}

	/// <summary>
	/// Automatically called each network update to capture server state.
	/// </summary>
	protected override void OnFixedUpdate()
	{
		// On server or for owner, we don't interpolate
		if ( Networking.IsHost || Network.IsOwner )
			return;

		// Capture the network-synchronized transform
		AddSnapshot( WorldPosition, WorldRotation );
	}

	private void Interpolate()
	{
		if ( _buffer.Count < 2 )
			return;

		var renderTime = Time.Now - InterpolationDelay;

		// Find the two snapshots to interpolate between
		TransformSnapshot? from = null;
		TransformSnapshot? to = null;

		for ( int i = 0; i < _buffer.Count - 1; i++ )
		{
			if ( _buffer[i].Time <= renderTime && _buffer[i + 1].Time >= renderTime )
			{
				from = _buffer[i];
				to = _buffer[i + 1];
				break;
			}
		}

		// If we couldn't find a valid pair, use the latest
		if ( !from.HasValue || !to.HasValue )
		{
			if ( _buffer.Count > 0 )
			{
				var latest = _buffer[^1];
				_targetPosition = latest.Position;
				_targetRotation = latest.Rotation;
			}
		}
		else
		{
			// Calculate interpolation factor
			var duration = to.Value.Time - from.Value.Time;
			var elapsed = renderTime - from.Value.Time;
			var t = duration > 0 ? elapsed / duration : 1f;
			t = t.Clamp( 0f, 1f );

			_targetPosition = Vector3.Lerp( from.Value.Position, to.Value.Position, t );
			_targetRotation = Rotation.Lerp( from.Value.Rotation, to.Value.Rotation, t );
		}

		// Check for teleport
		var distance = WorldPosition.Distance( _targetPosition );
		if ( distance > TeleportThreshold )
		{
			WorldPosition = _targetPosition;
			WorldRotation = _targetRotation;
		}
		else
		{
			// Smooth movement to target
			WorldPosition = _targetPosition;
			WorldRotation = _targetRotation;
		}
	}

	/// <summary>
	/// Clear the interpolation buffer. Useful after teleports.
	/// </summary>
	public void ClearBuffer()
	{
		_buffer.Clear();
		_initialized = false;
	}
}
