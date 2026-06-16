using Sandbox.Network;

namespace Sandbox;

internal static class PredictionSnapshotEncoding
{
	const byte FlagHostDirect = 1;

	public static byte[] Wrap( byte[] value, bool hostDirect, uint commandNumber )
	{
		var size = 1 + value.Length;
		if ( !hostDirect )
			size += 4;

		var result = new byte[size];
		result[0] = hostDirect ? FlagHostDirect : (byte)0;

		var offset = 1;
		if ( !hostDirect )
		{
			BitConverter.TryWriteBytes( result.AsSpan( offset, 4 ), commandNumber );
			offset += 4;
		}

		value.CopyTo( result, offset );
		return result;
	}

	public static bool TryUnwrap( byte[] wrapped, out bool hostDirect, out uint commandNumber, out byte[] value )
	{
		hostDirect = false;
		commandNumber = 0;
		value = wrapped;

		if ( wrapped is null || wrapped.Length == 0 )
			return false;

		hostDirect = (wrapped[0] & FlagHostDirect) != 0;

		if ( hostDirect )
		{
			value = wrapped.Length > 1 ? wrapped[1..] : Array.Empty<byte>();
			return true;
		}

		if ( wrapped.Length < 5 )
			return false;

		commandNumber = BitConverter.ToUInt32( wrapped, 1 );
		value = wrapped[5..];
		return true;
	}

	public static bool IsWrapped( byte[] bytes )
	{
		return bytes is { Length: > 0 };
	}

	public static ulong Hash( byte[] value )
	{
		return Sandbox.Hashing.XxHash3.HashToUInt64( value );
	}
}
