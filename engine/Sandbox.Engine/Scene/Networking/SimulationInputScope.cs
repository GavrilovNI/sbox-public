namespace Sandbox;

public static partial class Input
{
	internal static Connection SimulationInputConnection;
	internal static int SimulationInputScopeDepth;

	internal static bool IsSimulationInputActive => SimulationInputScopeDepth > 0;
}

internal sealed class SimulationInputScopeImpl : IDisposable
{
	readonly GameObject _gameObject;
	readonly Connection _inputConnection;
	readonly ulong _savedActions;
	readonly Vector3 _savedAnalogMove;
	readonly Angles _savedAnalogLook;
	readonly Connection _savedSimulationConnection;
	readonly int _scopeDepth;
	readonly bool _active;

	public SimulationInputScopeImpl( GameObject gameObject )
	{
		_gameObject = gameObject;

		var ownerId = gameObject.Network.OwnerId;
		_inputConnection = ownerId != Guid.Empty ? gameObject.Network.Owner : Connection.Local;

		_active = gameObject._net is not null
			&& gameObject._net.HasPrediction
			&& _inputConnection is not null
			&& _inputConnection != Connection.Local;

		if ( !_active )
			return;

		_savedSimulationConnection = Input.SimulationInputConnection;
		_savedActions = Input.Actions;
		_savedAnalogMove = Input.AnalogMove;
		_savedAnalogLook = Input.AnalogLook;
		_scopeDepth = Input.SimulationInputScopeDepth;

		Input.SimulationInputConnection = _inputConnection;
		Input.SimulationInputScopeDepth = _scopeDepth + 1;
		Input.Actions = _inputConnection.Input.Actions;
		Input.AnalogMove = _inputConnection.Input.AnalogMove;
		Input.AnalogLook = _inputConnection.Input.AnalogLook;
	}

	public void Dispose()
	{
		if ( !_active )
			return;

		if ( Input.SimulationInputScopeDepth != _scopeDepth + 1 )
			return;

		Input.SimulationInputScopeDepth = _scopeDepth;
		Input.SimulationInputConnection = _savedSimulationConnection;
		Input.Actions = _savedActions;
		Input.AnalogMove = _savedAnalogMove;
		Input.AnalogLook = _savedAnalogLook;
	}
}
