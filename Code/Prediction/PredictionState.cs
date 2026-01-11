namespace Prediction;

/// <summary>
/// Represents a snapshot of the predicted state at a specific tick.
/// </summary>
public struct PredictionState
{
	public int Tick;
	public float Time;
	public Vector3 Position;
	public Rotation Rotation;
	public Vector3 Velocity;

	// Add more state fields as needed (health, ammo, etc.)

	public readonly bool Equals( PredictionState other, float tolerance = 0.01f )
	{
		return Position.Distance( other.Position ) < tolerance
		       && Velocity.Distance( other.Velocity ) < tolerance;
	}
}
