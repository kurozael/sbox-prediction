namespace Prediction.Example;
/// <summary>
/// Example input struct - extend with your game-specific inputs.
/// </summary>
public struct PlayerInput : IPredictionInput
{
	public int Tick { get; set; }
	public Vector3 MoveDirection { get; set; }
	public Angles ViewAngles { get; set; }
	public bool Run { get; set; }
	public bool Jump { get; set; }
	public bool Attack { get; set; }
	public bool Use { get; set; }
}

/// <summary>
/// Example state struct - extend with your game-specific state.
/// </summary>
public struct PlayerState : IPredictionState
{
	public int Tick { get; set; }
	public float Time { get; set; }
	public Vector3 Position { get; set; }
	public Rotation Rotation { get; set; }
	public Vector3 Velocity { get; set; }
	public bool IsGrounded { get; set; }

	public readonly bool Equals( IPredictionState other, float tolerance )
	{
		if ( other is not PlayerState otherState )
			return false;

		var positionDiff = (Position - otherState.Position).Length;
		return positionDiff <= tolerance;
	}
}
