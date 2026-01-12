using System;
using Sandbox;

namespace Prediction;

/// <summary>
/// Non-generic base class for PredictionController so the system can track all controllers.
/// </summary>
public interface IPredictionController : IValid
{
	bool IsLocalController { get; }
	void ProcessServerInputQueue();
	void Simulate();
	void UpdateInterpolation();
}

/// <summary>
/// Centralized system for managing client-side prediction across all predicted entities.
/// Uses manual tick accumulation in Update instead of FixedUpdate for more control.
/// </summary>
public sealed class PredictionSystem : GameObjectSystem<PredictionSystem>
{
	/// <summary>
	/// The current simulation tick for the local client.
	/// </summary>
	public int CurrentTick { get; private set; }

	/// <summary>
	/// The last tick acknowledged by the server.
	/// </summary>
	public int LastAcknowledgedTick { get; private set; }

	/// <summary>
	/// Server's current tick (synchronized from host).
	/// </summary>
	public int ServerTick { get; private set; }

	/// <summary>
	/// Whether the client has synchronized with the server's tick.
	/// </summary>
	public bool IsSynchronized { get; private set; }

	/// <summary>
	/// Target number of ticks the client should be ahead of the server.
	/// This provides a buffer for network latency.
	/// </summary>
	public int TargetTickAhead { get; set; } = 2;

	/// <summary>
	/// Maximum tick difference before forcing a resync.
	/// </summary>
	public int MaxTickDrift { get; set; } = 30;

	/// <summary>
	/// Fixed timestep for simulation in seconds.
	/// </summary>
	public float TickInterval { get; set; } = 1f / 30f;

	/// <summary>
	/// Maximum ticks to simulate per frame to prevent spiral of death.
	/// </summary>
	public int MaxTicksPerFrame { get; set; } = 5;

	/// <summary>
	/// Accumulated time since last tick.
	/// </summary>
	private float _accumulator;

	/// <summary>
	/// All registered prediction controllers.
	/// </summary>
	private readonly List<IPredictionController> _controllers = [];

	public PredictionSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, -100, OnPreUpdate, "PredictionSystem.PreUpdate" );
		Listen( Stage.FinishUpdate, 100, OnPostUpdate, "PredictionSystem.PostUpdate" );
	}

	/// <summary>
	/// Register a controller with the system.
	/// </summary>
	public void Register( IPredictionController controller )
	{
		if ( !_controllers.Contains( controller ) )
		{
			_controllers.Add( controller );
		}
	}

	/// <summary>
	/// Unregister a controller from the system.
	/// </summary>
	public void Unregister( IPredictionController controller )
	{
		_controllers.Remove( controller );
	}

	/// <summary>
	/// Called when the server acknowledges a tick.
	/// </summary>
	public void AcknowledgeTick( int tick )
	{
		if ( tick > LastAcknowledgedTick )
		{
			LastAcknowledgedTick = tick;
		}
	}

	/// <summary>
	/// Update the known server tick and synchronize client tick if needed.
	/// </summary>
	public void UpdateServerTick( int tick )
	{
		if ( Networking.IsHost )
			return;

		if ( tick <= ServerTick )
			return;

		ServerTick = tick;

		if ( !IsSynchronized )
		{
			CurrentTick = ServerTick + TargetTickAhead;
			IsSynchronized = true;
			return;
		}

		var tickDifference = CurrentTick - ServerTick;

		if ( tickDifference >= 0 && tickDifference <= MaxTickDrift )
			return;

		CurrentTick = ServerTick + TargetTickAhead;
		_accumulator = 0f;
	}

	/// <summary>
	/// Pre-update: clean up and prepare.
	/// </summary>
	private void OnPreUpdate()
	{
		_controllers.RemoveAll( c => !c.IsValid() );
	}

	/// <summary>
	/// Post-update: run simulation ticks and update visuals.
	/// </summary>
	private void OnPostUpdate()
	{
		var canSimulate = Networking.IsHost || IsSynchronized;
		if ( !canSimulate )
			return;

		_accumulator += Time.Delta;

		var ticksThisFrame = 0;

		while ( _accumulator >= TickInterval && ticksThisFrame < MaxTicksPerFrame )
		{
			SimulateTick();
			_accumulator -= TickInterval;
			ticksThisFrame++;
		}

		if ( _accumulator > TickInterval * MaxTicksPerFrame )
		{
			_accumulator = 0f;
		}

		UpdateInterpolation();
	}

	/// <summary>
	/// Run a single simulation tick.
	/// </summary>
	private void SimulateTick()
	{
		if ( Networking.IsHost )
		{
			foreach ( var controller in _controllers )
			{
				if ( !controller.IsValid() )
					continue;

				if ( !controller.IsLocalController )
				{
					controller.ProcessServerInputQueue();
				}
			}
		}

		foreach ( var controller in _controllers )
		{
			if ( !controller.IsValid() )
				continue;

			if ( controller.IsLocalController )
			{
				controller.Simulate();
			}
		}

		CurrentTick++;
	}

	/// <summary>
	/// Update visuals for all controllers.
	/// </summary>
	private void UpdateInterpolation()
	{
		foreach ( var controller in _controllers )
		{
			if ( !controller.IsValid() )
				continue;

			controller.UpdateInterpolation();
		}
	}
}
