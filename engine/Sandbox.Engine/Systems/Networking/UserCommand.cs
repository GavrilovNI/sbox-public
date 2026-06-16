namespace Sandbox;

/// <summary>
/// A User Command that will be sent to the current host every tick.
/// </summary>
internal struct UserCommand( uint commandNumber )
{
	private static uint s_nextAvailableCommandNumber = 1;

	/// <summary>
	/// The command number of this <see cref="UserCommand"/>.
	/// </summary>
	public uint CommandNumber { get; private set; } = commandNumber;

	/// <summary>
	/// Which actions are currently being held down.
	/// </summary>
	public ulong Actions;

	/// <summary>
	/// Owner's analog move at the last network tick.
	/// </summary>
	public Vector3 AnalogMove;

	/// <summary>
	/// Owner's analog look at the last network tick.
	/// </summary>
	public Angles AnalogLook;

	/// <summary>
	/// Serialize this <see cref="UserCommand"/> to the specified <see cref="ByteStream"/>.
	/// </summary>
	internal void Serialize( ref ByteStream bs )
	{
		bs.Write( CommandNumber );
		bs.Write( Actions );
		bs.Write( AnalogMove );
		bs.Write( AnalogLook );
	}

	/// <summary>
	/// Deserialize this <see cref="UserCommand"/> from specified <see cref="ByteStream"/>.
	/// </summary>
	internal void Deserialize( ref ByteStream bs )
	{
		CommandNumber = bs.Read<uint>();
		Actions = bs.Read<ulong>();

		if ( bs.ReadRemaining > 0 )
		{
			AnalogMove = bs.Read<Vector3>();
			AnalogLook = bs.Read<Angles>();
		}
	}

	/// <summary>
	/// Reset the next available command number back to zero.
	/// </summary>
	internal static void Reset()
	{
		s_nextAvailableCommandNumber = 1;
	}

	/// <summary>
	/// Create a new <see cref="UserCommand"/> with the next available command number.
	/// </summary>
	/// <returns></returns>
	public static UserCommand Create()
	{
		return new UserCommand( s_nextAvailableCommandNumber++ );
	}
}
