namespace Sandbox;

public static partial class Input
{
	internal static Connection SimulationInputConnection;
	internal static int SimulationInputScopeDepth;

	internal static bool IsSimulationInputActive => SimulationInputScopeDepth > 0;
}

internal sealed class SimulationInputScopeImpl : IDisposable
{
	readonly Connection _inputConnection;
	readonly Connection _savedSimulationConnection;
	readonly int _scopeDepth;
	readonly bool _active;

	public SimulationInputScopeImpl( GameObject gameObject )
	{
		var ownerId = gameObject.Network.OwnerId;
		_inputConnection = ownerId != Guid.Empty ? gameObject.Network.Owner : Connection.Local;

		var networkObject = gameObject._net;
		_active = networkObject is not null
			&& networkObject.HasPrediction
			&& _inputConnection is not null
			&& (_inputConnection != Connection.Local || networkObject.ShouldPredictLocally);

		if ( !_active )
			return;

		_savedSimulationConnection = Input.SimulationInputConnection;
		_scopeDepth = Input.SimulationInputScopeDepth;

		Input.SimulationInputConnection = _inputConnection;
		Input.SimulationInputScopeDepth = _scopeDepth + 1;
	}

	public void Dispose()
	{
		if ( !_active )
			return;

		if ( Input.SimulationInputScopeDepth != _scopeDepth + 1 )
			return;

		Input.SimulationInputScopeDepth = _scopeDepth;
		Input.SimulationInputConnection = _savedSimulationConnection;
	}
}
