using Sandbox;

namespace Prediction;

/// <summary>
/// Example player controller demonstrating how to use the prediction system.
/// This component should be on the same GameObject as PredictionController.
/// </summary>
public sealed class ExamplePredictedPlayer : Component, IPredicted
{
	[Property]
	public float MoveSpeed { get; set; } = 200f;

	[Property]
	public float JumpForce { get; set; } = 300f;

	[Property]
	public float Gravity { get; set; } = 800f;

	public bool IsOnGround => CharacterController.IsOnGround;
	public Vector3 Velocity => CharacterController.Velocity;

	[Property]
	public CharacterController CharacterController { get; set; }

	private PredictionController _prediction;
	private Vector3 _velocity;
	private bool _isGrounded;

	protected override void OnStart()
	{
		_prediction = Components.Get<PredictionController>();
		CharacterController ??= Components.Get<CharacterController>();

		// Setup is now handled by the spawning code which calls SetController()
		// The PredictionController will handle NoTransformSync automatically
	}

	protected override void OnUpdate()
	{
		// Only the controlling client builds input
		if ( _prediction is not { IsLocalController: true } )
			return;

		// Build input from player's actual input devices
		_prediction.BuildInput();

		// Or build custom input:
		// _prediction?.SetInput( new PredictionInput
		// {
		//     MoveDirection = Input.AnalogMove,
		//     ViewAngles = Input.AnalogLook,
		//     Jump = Input.Down( "jump" )
		// } );
	}

	/// <summary>
	/// This is called by the prediction system for simulation.
	/// Runs on client for prediction, and on server for authority.
	/// Must be deterministic - same input should produce same output!
	/// </summary>
	public void OnSimulate( PredictionInput input )
	{
		if ( CharacterController == null )
			return;

		// Check ground state
		_isGrounded = CharacterController.IsOnGround;

		// Build wish velocity from input
		var wishDir = input.MoveDirection.Normal;
		var wishVelocity = wishDir * MoveSpeed;

		// Transform to world space based on view angles
		var viewRotation = Rotation.FromYaw( input.ViewAngles.yaw );
		wishVelocity = viewRotation * wishVelocity;

		// Apply movement
		if ( _isGrounded )
		{
			_velocity = wishVelocity;

			// Handle jump
			if ( input.Jump )
			{
				_velocity += Vector3.Up * JumpForce;
				_isGrounded = false;
			}
		}
		else
		{
			// Air control (reduced)
			_velocity += wishVelocity * 0.1f * Time.Delta;

			// Apply gravity
			_velocity += Vector3.Down * Gravity * Time.Delta;
		}

		// Move the character
		CharacterController.Velocity = _velocity;
		CharacterController.Move();

		// Update velocity from character controller (handles collisions)
		_velocity = CharacterController.Velocity;
	}

	/// <summary>
	/// Called when the server corrects our prediction.
	/// Use this to reset any visual effects or sounds that shouldn't repeat.
	/// </summary>
	public void OnReconcile()
	{
		// Example: You might want to:
		// - Cancel footstep sounds that were predicted
		// - Reset particle effects
		// - Clear jump animations if we didn't actually jump

		Log.Info( "Reconciliation occurred - prediction was corrected" );
	}

	/// <summary>
	/// For rendering, use the visual position to get smooth reconciliation.
	/// </summary>
	public Vector3 GetRenderPosition()
	{
		return _prediction?.GetVisualPosition() ?? WorldPosition;
	}

	/// <summary>
	/// Example: How to spawn a predicted player on the host.
	/// Call this when a player connects.
	/// </summary>
	public static void SetupPrediction( GameObject gameObject, Connection controller )
	{
		if ( !Networking.IsHost )
		{
			Log.Warning( "SetupPrediction should only be called on the host!" );
			return;
		}

		// Add required components
		var prediction = gameObject.Components.GetOrCreate<PredictionController>();
		gameObject.Components.GetOrCreate<ExamplePredictedPlayer>();

		// Set the controller - this syncs to all clients
		prediction.SetController( controller );
	}
}
