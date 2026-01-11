using System.Threading.Tasks;
using Prediction.Example;
using Sandbox.Network;

public class NetworkManager : Component, Component.INetworkListener
{
	[Property] public GameObject PlayerPrefab { get; set; }

	protected override Task OnLoad()
	{
		if ( !Networking.IsActive )
		{
			Networking.CreateLobby( new LobbyConfig() );
		}

		return Task.CompletedTask;
	}

	void INetworkListener.OnActive( Connection channel )
	{
		var player = PlayerPrefab.Clone();

		ExamplePredictedPlayer.SetupPrediction( player, channel );

		player.NetworkSpawn();
	}
}
