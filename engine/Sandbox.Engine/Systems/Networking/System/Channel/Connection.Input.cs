namespace Sandbox;

public abstract partial class Connection
{
	/// <summary>
	/// Build the <see cref="UserCommand"/> for this <see cref="Connection"/>.
	/// </summary>
	internal void BuildUserCommand( ref UserCommand cmd )
	{
		cmd.Actions = Sandbox.Input.Actions;
		cmd.AnalogMove = Sandbox.Input.AnalogMove;
		cmd.AnalogLook = Sandbox.Input.AnalogLook;
	}

	/// <summary>
	/// Apply one queued user command for this fixed update (host only).
	/// </summary>
	internal void ConsumeFixedUpdateUserCommand()
	{
		if ( !_pendingUserCommands.TryDequeue( out var cmd ) )
			return;

		Input.ApplyUserCommand( cmd );
	}

	/// <summary>
	/// Apply one queued user command per remote connection (host only).
	/// </summary>
	internal static void ConsumeAllFixedUpdateUserCommands()
	{
		foreach ( var connection in All )
		{
			if ( connection == Local )
				continue;

			connection.ConsumeFixedUpdateUserCommand();
		}
	}

	/// <summary>
	/// Clear pending pressed and released actions for all connections in the Update context.
	/// </summary>
	internal static void ClearUpdateContextInput()
	{
		foreach ( var connection in All )
		{
			connection.Input.ClearUpdateContext();
		}
	}

	/// <summary>
	/// Clear pending pressed and released actions for all connections in the Fixed Update context.
	/// </summary>
	internal static void ClearFixedUpdateContextInput()
	{
		foreach ( var connection in All )
		{
			connection.Input.ClearFixedUpdateContext();
		}
	}

	readonly Queue<UserCommand> _pendingUserCommands = new();
	uint _lastQueuedUserCommandNumber;

	internal void QueueUserCommand( in UserCommand cmd )
	{
		var referenceNumber = _pendingUserCommands.Count > 0
			? _lastQueuedUserCommandNumber
			: Input.LastCommandNumber;

		var commandNumberDelta = cmd.CommandNumber - referenceNumber;

		if ( commandNumberDelta is 0 or > 0x7FFFFFFF )
			return;

		_pendingUserCommands.Enqueue( cmd );
		_lastQueuedUserCommandNumber = cmd.CommandNumber;
	}

	internal struct InputState
	{
		internal struct Context
		{
			public ulong Pressed;
			public ulong Released;

			/// <summary>
			/// Clear the state of this input context.
			/// </summary>
			public void Clear()
			{
				Released = 0;
				Pressed = 0;
			}
		}

		private UserCommand _lastUserCommand;

		public ulong Actions;

		public Vector3 AnalogMove;
		public Angles AnalogLook;

		public uint LastCommandNumber => _lastUserCommand.CommandNumber;

		private Context _fixedUpdateContext;
		private Context _updateContext;

		public Context GetCurrentContext()
		{
			var isFixedUpdate = Game.ActiveScene?.IsFixedUpdate ?? false;
			return isFixedUpdate ? _fixedUpdateContext : _updateContext;
		}

		public void ApplyUserCommand( in UserCommand cmd )
		{
			var commandNumberDelta = cmd.CommandNumber - _lastUserCommand.CommandNumber;

			// Drop duplicates or commands that are too far behind (wrap-aware)
			if ( commandNumberDelta is 0 or > 0x7FFFFFFF )
				return;

			var pressed = (~_lastUserCommand.Actions) & cmd.Actions;
			var released = _lastUserCommand.Actions & ~cmd.Actions;

			if ( pressed != 0 )
			{
				_fixedUpdateContext.Pressed |= pressed;
				_updateContext.Pressed |= pressed;
			}

			if ( released != 0 )
			{
				_fixedUpdateContext.Released |= released;
				_updateContext.Released |= released;
			}

			Actions = cmd.Actions;
			AnalogMove = cmd.AnalogMove;
			AnalogLook = cmd.AnalogLook;

			_lastUserCommand = cmd;
		}

		public void ClearFixedUpdateContext()
		{
			_fixedUpdateContext.Clear();
		}

		public void ClearUpdateContext()
		{
			_updateContext.Clear();
		}

		public void Clear()
		{
			_lastUserCommand = default;
			AnalogMove = default;
			AnalogLook = default;
		}
	}

	internal InputState Input;

	/// <summary>
	/// Action is currently pressed down for this <see cref="Connection"/>.
	/// </summary>
	public bool Down( [InputAction] string action )
	{
		if ( Local == this && Sandbox.Input.SimulationInputConnection != this )
			return Sandbox.Input.Down( action );

		if ( string.IsNullOrWhiteSpace( action ) )
			return false;

		var index = Sandbox.Input.GetActionIndex( action );
		if ( index == -1 )
			return false;

		var mask = 1UL << index;
		return (Input.Actions & mask) != 0;
	}

	/// <summary>
	/// Action was pressed for this <see cref="Connection"/> within the current update context.
	/// </summary>
	public bool Pressed( [InputAction] string action )
	{
		if ( Local == this && Sandbox.Input.SimulationInputConnection != this )
			return Sandbox.Input.Pressed( action );

		if ( string.IsNullOrWhiteSpace( action ) )
			return false;

		var index = Sandbox.Input.GetActionIndex( action );
		if ( index == -1 )
			return false;

		var mask = 1UL << index;
		var context = Input.GetCurrentContext();

		return (context.Pressed & mask) != 0;
	}

	/// <summary>
	/// Action was released for this <see cref="Connection"/> within the current update context.
	/// </summary>
	public bool Released( [InputAction] string action )
	{
		if ( Local == this && Sandbox.Input.SimulationInputConnection != this )
			return Sandbox.Input.Released( action );

		if ( string.IsNullOrWhiteSpace( action ) )
			return false;

		var index = Sandbox.Input.GetActionIndex( action );
		if ( index == -1 )
			return false;

		var mask = 1UL << index;
		var context = Input.GetCurrentContext();

		return (context.Released & mask) != 0;
	}
}
