namespace Prediction;

/// <summary>
/// Base interface for prediction state structs.
/// Implement this on your custom state struct.
/// </summary>
public interface IPredictionState
{
	/// <summary>
	/// The tick this state was captured at.
	/// </summary>
	int Tick { get; set; }

	/// <summary>
	/// The time this state was captured at.
	/// </summary>
	float Time { get; set; }

	/// <summary>
	/// The position at this state.
	/// </summary>
	Vector3 Position { get; set; }

	/// <summary>
	/// The rotation at this state.
	/// </summary>
	Rotation Rotation { get; set; }

	/// <summary>
	/// Check if this state equals another within tolerance.
	/// </summary>
	bool Equals( IPredictionState other, float tolerance );
}
