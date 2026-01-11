using Sandbox;

namespace Prediction;

/// <summary>
/// Example player controller demonstrating how to use the prediction system.
/// This component should be on the same GameObject as PredictionController.
/// </summary>
public sealed class ExamplePredictedPlayer : Component, IPredicted
{
	[Property] public float MoveSpeed { get; set; } = 200f;
	[Property] public float JumpForce { get; set; } = 300f;
	[Property] public float Gravity { get; set; } = 800f;
	[Property] public CharacterController CharacterController { get; set; }

	private PredictionController _prediction;
	private Vector3 _velocity;
	private bool _isGrounded;

	protected override void OnStart()
	{
		_prediction = Components.Get<PredictionController>();
		CharacterController ??= Components.Get<CharacterController>();
	}

	void IPredicted.CaptureState( ref PredictionState state )
	{
		state.Velocity = _velocity;
		state.IsGrounded = _isGrounded;
	}

	void IPredicted.ApplyState( PredictionState state )
	{
		_velocity = state.Velocity;
		_isGrounded = state.IsGrounded;

		CharacterController?.Velocity = _velocity;
	}

	void IPredicted.BuildInput( ref PredictionInput input )
	{
		input.MoveDirection = Input.AnalogMove;
		input.ViewAngles = Input.AnalogLook;
		input.Run = Input.Down( "run" );
		input.Jump = Input.Down( "jump" );
		input.Attack = Input.Down( "attack1" );
		input.Use = Input.Down( "use" );
	}

	void IPredicted.OnSimulate( PredictionInput input )
	{
		if ( CharacterController == null )
			return;

		// Grounded comes from the controller's collision state.
		_isGrounded = CharacterController.IsOnGround;

		var moveSpeed = MoveSpeed;
		if ( input.Run ) moveSpeed *= 4f;

		var wishDir = input.MoveDirection.Normal;
		var wishVelocity = wishDir * moveSpeed;

		var viewRotation = Rotation.FromYaw( input.ViewAngles.yaw );
		wishVelocity = viewRotation * wishVelocity;

		if ( _isGrounded )
		{
			_velocity = wishVelocity;

			if ( input.Jump )
			{
				_velocity += Vector3.Up * JumpForce;
				_isGrounded = false;
			}
		}
		else
		{
			_velocity += wishVelocity * 0.1f * Time.Delta;
			_velocity += Vector3.Down * Gravity * Time.Delta;
		}

		CharacterController.Velocity = _velocity;
		CharacterController.Move();

		_velocity = CharacterController.Velocity;
		_isGrounded = CharacterController.IsOnGround;
	}

	void IPredicted.OnReconcile()
	{
		Log.Info( "Reconciliation occurred - prediction was corrected" );
	}

	public static void SetupPrediction( GameObject gameObject, Connection controller )
	{
		if ( !Networking.IsHost )
		{
			Log.Warning( "SetupPrediction should only be called on the host!" );
			return;
		}

		var prediction = gameObject.Components.GetOrCreate<PredictionController>();
		gameObject.Components.GetOrCreate<ExamplePredictedPlayer>();

		prediction.SetController( controller );
	}
}
