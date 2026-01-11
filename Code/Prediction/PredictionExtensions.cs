namespace Prediction;

/// <summary>
/// Extension methods for easier prediction setup.
/// </summary>
public static class PredictionExtensions
{
	extension ( GameObject gameObject )
	{
		/// <summary>
		/// Set up a GameObject for client-side prediction.
		/// </summary>
		public void SetupPrediction()
		{
			// Disable automatic transform sync
			gameObject.Network.Flags |= NetworkFlags.NoTransformSync;

			// Add a prediction controller if not present
			gameObject.Components.GetOrCreate<PredictionController>();
		}

		/// <summary>
		/// Set up a GameObject for interpolated remote viewing.
		/// </summary>
		public void SetupInterpolation()
		{
			gameObject.Components.GetOrCreate<TransformInterpolator>();
		}
	}
}
