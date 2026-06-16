namespace Sandbox;

internal struct PredictionHistoryEntry
{
	public uint CommandNumber;
	public byte[] Bytes;
}

/// <summary>
/// Fixed-capacity ring buffer for predicted value history. Lazy-allocated, zero steady-state GC.
/// </summary>
internal struct PredictionHistoryBuffer
{
	public const int Capacity = 128;

	PredictionHistoryEntry[] _entries;
	int _head;
	int _count;

	public int Count => _count;

	public bool IsAllocated => _entries is not null;

	void EnsureAllocated()
	{
		_entries ??= new PredictionHistoryEntry[Capacity];
	}

	public void Clear()
	{
		_head = 0;
		_count = 0;
	}

	public void Push( uint commandNumber, byte[] bytes )
	{
		EnsureAllocated();

		if ( _count < Capacity )
		{
			var index = (_head + _count) % Capacity;
			_entries[index] = new PredictionHistoryEntry { CommandNumber = commandNumber, Bytes = bytes };
			_count++;
			return;
		}

		_entries[_head] = new PredictionHistoryEntry { CommandNumber = commandNumber, Bytes = bytes };
		_head = (_head + 1) % Capacity;
	}

	public void Truncate( uint commandNumber )
	{
		if ( _entries is null || _count == 0 )
			return;

		var writeIndex = 0;
		for ( var i = 0; i < _count; i++ )
		{
			var index = (_head + i) % Capacity;
			var entry = _entries[index];

			if ( entry.CommandNumber > commandNumber )
			{
				if ( writeIndex != i )
					_entries[(_head + writeIndex) % Capacity] = entry;

				writeIndex++;
			}
		}

		_count = writeIndex;
	}

	public bool TryGetLastCommandNumber( out uint commandNumber )
	{
		if ( _count == 0 )
		{
			commandNumber = 0;
			return false;
		}

		var index = (_head + _count - 1) % Capacity;
		commandNumber = _entries[index].CommandNumber;
		return true;
	}

	public void Replay( Action<byte[]> apply )
	{
		if ( _entries is null )
			return;

		for ( var i = 0; i < _count; i++ )
		{
			var index = (_head + i) % Capacity;
			apply( _entries[index].Bytes );
		}
	}
}
