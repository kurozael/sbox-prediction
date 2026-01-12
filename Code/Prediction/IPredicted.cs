namespace Prediction;

/// <summary>
/// Interface for components that need client-side prediction.
/// </summary>
/// <typeparam name="TInput">Your custom input struct implementing <see cref="IPredictionInput"/>.</typeparam>
/// <typeparam name="TState">Your custom state struct implementing <see cref="IPredictionState"/>.</typeparam>
public interface IPredicted<TInput, TState>
	where TInput : struct, IPredictionInput
	where TState : struct, IPredictionState
{
	/// <summary>
	/// Called every simulation tick with the current input.
	/// Implement your movement/action logic here.
	/// </summary>
	void OnSimulate( TInput input );

	/// <summary>
	/// Build the input for the current frame.
	/// </summary>
	void BuildInput( ref TInput input );

	/// <summary>
	/// Capture the current state into the provided struct.
	/// </summary>
	void WriteState( ref TState state );

	/// <summary>
	/// Apply a state to restore simulation.
	/// </summary>
	void ReadState( TState state );

	/// <summary>
	/// Optional: Called when the server corrects our predicted state.
	/// </summary>
	void OnReconcile( TState serverState, TState predictedState ) { }
}
