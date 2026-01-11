using System;

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
	[Property] public float GroundAcceleration { get; set; } = 10f;
	[Property] public float GroundFriction { get; set; } = 8f;
	[Property] public float AirAcceleration { get; set; } = 0.5f;
	[Property] public CharacterController CharacterController { get; set; }

	private Vector3 _velocity;
	private bool _isGrounded;

	protected override void OnStart()
	{
		CharacterController ??= Components.Get<CharacterController>();
	}

	protected override void OnUpdate()
	{
		Scene.Camera.WorldPosition = WorldPosition + Vector3.Up * 100f + Vector3.Backward * 400f;
		Scene.Camera.WorldRotation = Rotation.LookAt( WorldPosition - Scene.Camera.WorldPosition, Vector3.Up );

		base.OnUpdate();
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

		if ( CharacterController != null )
		{
			CharacterController.Velocity = _velocity;
		}
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

		_isGrounded = CharacterController.IsOnGround;

		var moveSpeed = MoveSpeed;
		if ( input.Run ) moveSpeed *= 4f;

		var wishDir = input.MoveDirection.Normal;
		var viewRotation = Rotation.FromYaw( input.ViewAngles.yaw );
		var wishVelocity = viewRotation * (wishDir * moveSpeed);

		if ( _isGrounded )
		{
			// Apply friction first
			var speed = _velocity.WithZ( 0 ).Length;
			if ( speed > 0.1f )
			{
				var drop = speed * GroundFriction * Time.Delta;
				var scale = MathF.Max( speed - drop, 0 ) / speed;
				_velocity.x *= scale;
				_velocity.y *= scale;
			}
			else
			{
				_velocity.x = 0;
				_velocity.y = 0;
			}

			// Accelerate toward wish velocity
			var currentSpeed = Vector3.Dot( _velocity, wishVelocity.Normal );
			var addSpeed = moveSpeed - currentSpeed;

			if ( addSpeed > 0 )
			{
				var accelSpeed = GroundAcceleration * moveSpeed * Time.Delta;
				accelSpeed = MathF.Min( accelSpeed, addSpeed );
				_velocity += wishVelocity.Normal * accelSpeed;
			}

			if ( input.Jump )
			{
				_velocity += Vector3.Up * JumpForce;
				_isGrounded = false;
			}
		}
		else
		{
			// Air movement
			var currentSpeed = Vector3.Dot( _velocity.WithZ( 0 ), wishVelocity.Normal );
			var addSpeed = moveSpeed - currentSpeed;

			if ( addSpeed > 0 )
			{
				var accelSpeed = AirAcceleration * moveSpeed * Time.Delta;
				accelSpeed = MathF.Min( accelSpeed, addSpeed );
				_velocity += wishVelocity.Normal * accelSpeed;
			}

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
