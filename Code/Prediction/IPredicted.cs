namespace Prediction;

/// <summary>
/// Interface for components that need client-side prediction.
/// </summary>
public interface IPredicted
{
	/// <summary>
	/// Called every simulation tick with the current input.
	/// Implement your movement/action logic here.
	/// </summary>
	/// <param name="input">The input for this tick.</param>
	void OnSimulate( PredictionInput input );

	void BuildInput( ref PredictionInput input );
	void CaptureState( ref PredictionState state );
	void ApplyState( PredictionState state );

	/// <summary>
	/// Optional: Called when the server corrects our predicted state.
	/// Use this to handle any side effects of reconciliation.
	/// </summary>
	void OnReconcile() { }
}
