namespace Prediction;

/// <summary>
/// Base interface for prediction input structs.
/// Implement this on your custom input struct.
/// </summary>
public interface IPredictionInput
{
	/// <summary>
	/// The tick this input is for.
	/// </summary>
	int Tick { get; set; }
}

