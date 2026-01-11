namespace Prediction;

/// <summary>
/// Represents a snapshot of input at a specific tick.
/// Extend this struct with your game-specific inputs.
/// </summary>
public struct PredictionInput
{
	public int Tick;
	public Vector3 MoveDirection;
	public Angles ViewAngles;
	public bool Jump;
	public bool Attack;
	public bool Use;

	// Add more input fields as needed for your game
}
