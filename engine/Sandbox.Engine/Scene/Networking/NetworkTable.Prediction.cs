using Sandbox;

namespace Sandbox.Network;

internal partial class NetworkTable
{
	internal bool HasAnyPredictedSlot()
	{
		foreach ( var (_, entry) in _entries )
		{
			if ( entry.IsPredicted )
				return true;
		}

		return false;
	}

	internal bool TryGetEntry( int slot, out Entry entry )
	{
		return _entries.TryGetValue( slot, out entry );
	}

	internal bool CanWriteSnapshotEntry( Entry entry )
	{
		if ( entry.HasControl( Connection.Local ) )
			return true;

		if ( !entry.IsPredicted )
			return false;

		var net = PredictionContext.CurrentNetworkObject;
		if ( net is null || !net.HasPrediction )
			return false;

		if ( Networking.IsHost )
			return true;

		return net.IsOwner && !Networking.IsHost;
	}

	internal bool CanApplySnapshotEntry( Entry entry, Connection source )
	{
		if ( entry.HasControl( source ) )
			return true;

		if ( !entry.IsPredicted || !source.IsHost )
			return false;

		var net = PredictionContext.CurrentNetworkObject;
		if ( net is null )
			return false;

		return net.IsOwner || !net.IsOwner;
	}

	internal bool ShouldCompareOwnerSnapshot( Entry entry, Connection source )
	{
		return Networking.IsHost
			&& entry.IsPredicted
			&& source is not null
			&& PredictionContext.CurrentNetworkObject?.HasControl( source ) == true;
	}

	internal byte[] SerializeEntryValue( Entry entry )
	{
		var bs = ByteStream.Create( 4096 );

		try
		{
			WriteEntryToStream( entry, ref bs );
			return bs.ToArray();
		}
		finally
		{
			bs.Dispose();
		}
	}

	internal void ApplyEntryBytes( int slot, Entry entry, byte[] bytes )
	{
		var bs = ByteStream.CreateReader( bytes );

		try
		{
			IsReadingChanges = true;
			ReadEntryFromStream( slot, entry, ref bs );
		}
		finally
		{
			IsReadingChanges = false;
			entry.IsDirty = false;
			bs.Dispose();
		}
	}

	internal void WriteSnapshotStatePrediction( LocalSnapshotState snapshot )
	{
		var net = PredictionContext.CurrentNetworkObject;

		for ( var i = 0; i < _snapshotEntries.Count; i++ )
		{
			var entry = _snapshotEntries[i];

			if ( !CanWriteSnapshotEntry( entry ) )
				continue;

			if ( entry.IsDeltaSnapshotType )
			{
				var value = entry.GetValue() as INetworkDeltaSnapshot;
				value?.WriteSnapshotState( entry.Slot, snapshot );
				continue;
			}

			if ( entry.Serialized is not null )
				continue;

			var bs = ByteStream.Create( 4096 );

			try
			{
				WriteEntryToStream( entry, ref bs );
				entry.Serialized = bs.ToArray();
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Error when getting value {entry.DebugName} - {e.Message}" );
			}

			bs.Dispose();

			var valueBytes = entry.Serialized;

			if ( Networking.IsHost && entry.IsPredicted && net is not null )
			{
				var hostDirect = net.CurrentWriteSource == PredictionWriteSource.HostDirect;
				var commandNumber = hostDirect ? 0u : net.GetOwnerCommandNumber();
				valueBytes = PredictionSnapshotEncoding.Wrap( valueBytes, hostDirect, commandNumber );
			}

			snapshot.AddSerialized( entry.Slot, valueBytes );

			NetworkDebugSystem.Current?.TrackSync( entry.DebugName, valueBytes.Length, outbound: true );
		}
	}

	internal void ReadSnapshotPrediction( Connection source, DeltaSnapshot snapshot )
	{
		var net = PredictionContext.CurrentNetworkObject;

		foreach ( var entry in _snapshotEntries )
		{
			if ( !entry.IsDeltaSnapshotType )
				continue;

			if ( ShouldCompareOwnerSnapshot( entry, source ) )
				continue;

			if ( !CanApplySnapshotEntry( entry, source ) )
				continue;

			var value = entry.GetValue() as INetworkDeltaSnapshot;
			value?.ReadSnapshot( entry.Slot, snapshot );
		}

		foreach ( var kv in snapshot.Entries )
		{
			var slot = kv.Slot;
			var serialized = kv.Value;

			if ( !_entries.TryGetValue( slot, out var entry ) )
				continue;

			if ( entry.IsReliableType || entry.IsDeltaSnapshotType )
				continue;

			if ( ShouldCompareOwnerSnapshot( entry, source ) )
			{
				net?.CompareOwnerPredictedSnapshot( slot, entry, serialized, source );
				continue;
			}

			if ( !CanApplySnapshotEntry( entry, source ) )
				continue;

			if ( entry.IsPredicted && source.IsHost && net is not null && net.IsOwner && !Networking.IsHost )
			{
				net.ApplyHostPredictedSnapshot( slot, entry, serialized );
				continue;
			}

			if ( entry.IsPredicted && source.IsHost && PredictionSnapshotEncoding.TryUnwrap( serialized, out _, out _, out var unwrapped ) )
				serialized = unwrapped;

			ApplyEntryBytes( slot, entry, serialized );

			NetworkDebugSystem.Current?.TrackSync( entry.DebugName, serialized.Length, outbound: false, source );
		}
	}
}

