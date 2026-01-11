using System;
using Sandbox;

namespace Prediction;

/// <summary>
/// Centralized system for managing client-side prediction across all predicted entities.
/// Handles global tick synchronization and coordinates simulation/reconciliation.
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
	/// Fixed timestep for simulation.
	/// </summary>
	public float FixedDelta => Scene.FixedDelta;

	/// <summary>
	/// All registered prediction controllers.
	/// </summary>
	private readonly List<PredictionController> _controllers = new();

	public PredictionSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.StartFixedUpdate, -100, OnPreFixedUpdate, "PredictionSystem.PreFixedUpdate" );
		Listen( Stage.FinishFixedUpdate, 100, OnPostFixedUpdate, "PredictionSystem.PostFixedUpdate" );
		Listen( Stage.StartUpdate, -100, OnPreUpdate, "PredictionSystem.PreUpdate" );
	}

	/// <summary>
	/// Register a controller with the system.
	/// </summary>
	public void Register( PredictionController controller )
	{
		if ( !_controllers.Contains( controller ) )
		{
			_controllers.Add( controller );
		}
	}

	/// <summary>
	/// Unregister a controller from the system.
	/// </summary>
	public void Unregister( PredictionController controller )
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

		// Initial synchronization: set client tick ahead of server
		if ( !IsSynchronized )
		{
			CurrentTick = ServerTick + TargetTickAhead;
			IsSynchronized = true;
			Log.Info( $"Prediction synchronized: ServerTick={ServerTick}, CurrentTick={CurrentTick}" );
			return;
		}

		// Check for excessive drift
		var tickDifference = CurrentTick - ServerTick;

		if ( tickDifference < 0 )
		{
			// Client is behind server - this is bad, resync
			Log.Warning( $"Client tick behind server! Resyncing. Client={CurrentTick}, Server={ServerTick}" );
			CurrentTick = ServerTick + TargetTickAhead;
		}
		else if ( tickDifference > MaxTickDrift )
		{
			// Client is way too far ahead - resync
			Log.Warning( $"Client tick drift too large! Resyncing. Client={CurrentTick}, Server={ServerTick}, Drift={tickDifference}" );
			CurrentTick = ServerTick + TargetTickAhead;
		}
	}

	/// <summary>
	/// Pre-fixed update: prepare for simulation.
	/// </summary>
	private void OnPreFixedUpdate()
	{
		// Clean up destroyed controllers
		_controllers.RemoveAll( c => !c.IsValid() );
	}

	/// <summary>
	/// Post-fixed update: advance the tick after all simulation.
	/// </summary>
	private void OnPostFixedUpdate()
	{
		// Host processes all remote controller inputs
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

		// Local controllers simulate (clients wait for sync, host always runs)
		var canSimulate = Networking.IsHost || IsSynchronized;

		if ( !canSimulate )
			return;

		{
			foreach ( var controller in _controllers )
			{
				if ( !controller.IsValid() )
					continue;

				if ( controller.IsLocalController )
				{
					controller.Simulate();
				}
			}

			// Advance tick for local simulation
			CurrentTick++;
		}
	}

	/// <summary>
	/// Pre-update: handle visual interpolation before rendering.
	/// </summary>
	private void OnPreUpdate()
	{
		foreach ( var controller in _controllers )
		{
			if ( controller == null || !controller.IsValid )
				continue;

			controller.UpdateVisuals();
		}
	}
}
