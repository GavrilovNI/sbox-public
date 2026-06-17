using Sandbox.Network;

namespace Sandbox;

internal static class PredictionContext
{
	[ThreadStatic]
	internal static NetworkObject CurrentNetworkObject;
}

internal sealed partial class NetworkObject
{
	internal bool HasPrediction { get; private set; }

	internal PredictionWriteSource CurrentWriteSource { get; set; }

	Dictionary<int, PredictionHistoryBuffer> _slotHistory;
	PredictionHistoryBuffer _transformHistory;

	uint _ownerReconcileCommandNumber;
	bool _pendingHostDirectSend;

	internal bool HasPredictTransform =>
		(GameObject.Network.Flags & NetworkFlags.PredictTransform) != 0;

	internal void RecalculateHasPrediction()
	{
		HasPrediction = HasPredictTransform || dataTable.HasAnyPredictedSlot();

		if ( !HasPrediction )
			ClearPredictionHistory();
	}

	internal void ClearPredictionHistory()
	{
		_slotHistory = null;
		_transformHistory.Clear();
		_ownerReconcileCommandNumber = 0;
	}

	PredictionHistoryBuffer GetSlotHistory( int slot )
	{
		_slotHistory ??= new Dictionary<int, PredictionHistoryBuffer>();
		if ( !_slotHistory.TryGetValue( slot, out var buffer ) )
		{
			buffer = default;
			_slotHistory[slot] = buffer;
		}

		return buffer;
	}

	internal bool ShouldPredictLocally =>
		IsOwner && !Networking.IsHost && HasPrediction;

	internal uint GetOwnerCommandNumber()
	{
		return Connection.Find( Owner )?.Input.LastCommandNumber ?? 0;
	}

	internal bool TryHandlePredictedSet<T>( int slot, in T value, Action<T> setter, NetworkTable.Entry entry )
	{
		if ( !entry.IsPredicted || !HasPrediction )
			return false;

		if ( NetworkTable.IsReadingChanges )
		{
			setter( value );
			return true;
		}

		if ( IsProxy && !Networking.IsHost )
			return true;

		if ( Networking.IsHost )
		{
			CurrentWriteSource = PredictionWriteSource.HostDirect;
			dataTable.UpdateSlotHash( slot, value );
			setter( value );
			CurrentWriteSource = PredictionWriteSource.None;
			_slotHistory?.Remove( slot );
			QueueHostDirectSend();
			return true;
		}

		if ( ShouldPredictLocally )
		{
			CurrentWriteSource = PredictionWriteSource.OwnerPredicted;
			dataTable.UpdateSlotHash( slot, value );
			setter( value );
			CurrentWriteSource = PredictionWriteSource.None;

			var bytes = dataTable.SerializeEntryValue( entry );
			var history = GetSlotHistory( slot );
			history.Push( GetOwnerCommandNumber(), bytes );
			_slotHistory[slot] = history;
			return true;
		}

		return false;
	}

	void QueueHostDirectSend()
	{
		if ( _pendingHostDirectSend )
			return;

		_pendingHostDirectSend = true;
		SendPendingHostDirect();
	}

	void SendPendingHostDirect()
	{
		_pendingHostDirectSend = false;

		if ( !GameObject.IsValid() || !Networking.IsHost )
			return;

		var system = SceneNetworkSystem.Instance;
		if ( system is null )
			return;

		system.DeltaSnapshots.Send( this, NetFlags.Reliable | NetFlags.SendImmediate, true );
	}

	internal void RecordPredictedTransform()
	{
		if ( !ShouldPredictLocally || !HasPredictTransform )
			return;

		var flags = GameObject.Network.Flags;
		var tx = GameObject.Transform.TargetLocal;

		using var writer = ByteStream.Create( 128 );

		writer.Write( (flags & NetworkFlags.NoPositionSync) == 0 );
		writer.Write( (flags & NetworkFlags.NoRotationSync) == 0 );
		writer.Write( (flags & NetworkFlags.NoScaleSync) == 0 );

		if ( (flags & NetworkFlags.NoPositionSync) == 0 )
			writer.Write( tx.Position );

		if ( (flags & NetworkFlags.NoRotationSync) == 0 )
			writer.Write( tx.Rotation );

		if ( (flags & NetworkFlags.NoScaleSync) == 0 )
			writer.Write( tx.Scale );

		_transformHistory.Push( GetOwnerCommandNumber(), writer.ToArray() );
	}

	internal void RecordHostDirectTransform()
	{
		_transformHistory.Clear();
		QueueHostDirectSend();
	}

	internal void OnTransformWritten()
	{
		if ( !HasPredictTransform )
			return;

		if ( ShouldPredictLocally )
		{
			RecordPredictedTransform();
			return;
		}

		if ( Networking.IsHost && IsProxy )
		{
			CurrentWriteSource = PredictionWriteSource.HostDirect;
			RecordHostDirectTransform();
			CurrentWriteSource = PredictionWriteSource.None;
		}
	}

	internal void CompareOwnerPredictedSnapshot( int slot, NetworkTable.Entry entry, byte[] serialized, Connection source )
	{
		if ( !Networking.IsHost || !entry.IsPredicted )
			return;

		var authoritativeBytes = dataTable.SerializeEntryValue( entry );
		var ownerHash = PredictionSnapshotEncoding.Hash( serialized );
		var authoritativeHash = PredictionSnapshotEncoding.Hash( authoritativeBytes );

		if ( ownerHash == authoritativeHash )
			return;

		_ownerReconcileCommandNumber = source.Input.LastCommandNumber;
		dataTable.UpdateSlotHash( slot, entry.GetValue() );
	}

	internal void ApplyHostPredictedSnapshot( int slot, NetworkTable.Entry entry, byte[] serialized )
	{
		if ( !PredictionSnapshotEncoding.TryUnwrap( serialized, out var hostDirect, out var commandNumber, out var value ) )
		{
			dataTable.ApplyEntryBytes( slot, entry, serialized );
			return;
		}

		if ( hostDirect )
		{
			ApplyAuthoritativePush( slot, entry, value );
			return;
		}

		var currentBytes = dataTable.SerializeEntryValue( entry );
		if ( PredictionSnapshotEncoding.Hash( currentBytes ) == PredictionSnapshotEncoding.Hash( value ) )
		{
			var history = GetSlotHistory( slot );
			history.Truncate( commandNumber );
			_slotHistory[slot] = history;
			return;
		}

		Reconcile( slot, entry, value, commandNumber, entry.DebugName );
	}

	internal void ApplyAuthoritativePush( int slot, NetworkTable.Entry entry, byte[] value )
	{
		var history = GetSlotHistory( slot );
		history.Clear();
		_slotHistory[slot] = history;

		dataTable.ApplyEntryBytes( slot, entry, value );
	}

	internal void Reconcile( int slot, NetworkTable.Entry entry, byte[] authoritativeBytes, uint commandNumber, string debugName )
	{
		Log.Warning(
			$"Prediction correction on {GameObject.Name}: {debugName} " +
			$"(cmd {commandNumber}) authoritative overwritten predicted state" );

		var history = GetSlotHistory( slot );
		history.Truncate( commandNumber );
		dataTable.ApplyEntryBytes( slot, entry, authoritativeBytes );

		history.Replay( bytes => dataTable.ApplyEntryBytes( slot, entry, bytes ) );
		_slotHistory[slot] = history;
	}

	internal bool OnSnapshotPrediction( Connection source, DeltaSnapshot snapshot )
	{
		if ( !HasPredictTransform )
			return false;

		if ( HasControl( source ) && Networking.IsHost )
			CompareOwnerPredictedTransform( source, snapshot );
		else if ( source.IsHost && IsOwner && !Networking.IsHost )
			ApplyHostPredictedTransform( snapshot );
		else if ( HasControl( source ) )
			ApplyControlTransform( snapshot );

		return true;
	}

	void ApplyControlTransform( DeltaSnapshot snapshot )
	{
		snapshot.TryGetValue<bool>( SnapshotInterpolationSlot, out var clearInterpolation );

		var didTransformChange = false;
		var transform = GameObject.Transform.TargetLocal;

		if ( snapshot.TryGetValue<Vector3>( SnapshotPositionSlot, out var position ) )
		{
			didTransformChange = true;
			transform.Position = position;
		}

		if ( snapshot.TryGetValue<Rotation>( SnapshotRotationSlot, out var rotation ) )
		{
			didTransformChange = true;
			transform.Rotation = rotation;
		}

		if ( snapshot.TryGetValue<Vector3>( SnapshotScaleSlot, out var scale ) )
		{
			didTransformChange = true;
			transform.Scale = scale;
		}

		if ( didTransformChange )
			GameObject.Transform.FromNetwork( transform, clearInterpolation );
		else if ( clearInterpolation )
			GameObject.Transform.ClearLocalInterpolation();

		if ( snapshot.TryGetValue<bool>( SnapshotEnabledSlot, out var enabled ) )
			GameObject.Enabled = enabled;
	}

	void CompareOwnerPredictedTransform( Connection source, DeltaSnapshot snapshot )
	{
		var authoritativeBytes = SerializeTransformBytes();
		var ownerBytes = ReadTransformBytesFromSnapshot( snapshot );

		if ( ownerBytes is null )
			return;

		if ( PredictionSnapshotEncoding.Hash( ownerBytes ) == PredictionSnapshotEncoding.Hash( authoritativeBytes ) )
			return;

		_ownerReconcileCommandNumber = source.Input.LastCommandNumber;
	}

	void ApplyHostPredictedTransform( DeltaSnapshot snapshot )
	{
		byte[] positionBytes = null;
		byte[] rotationBytes = null;
		byte[] scaleBytes = null;
		bool hostDirect = false;
		uint commandNumber = 0;

		if ( snapshot.Lookup.TryGetValue( SnapshotPositionSlot, out var positionEntry ) )
			TryParseTransformSlot( positionEntry.Value, out hostDirect, out commandNumber, out positionBytes );

		if ( snapshot.Lookup.TryGetValue( SnapshotRotationSlot, out var rotationEntry ) )
			TryParseTransformSlot( rotationEntry.Value, out hostDirect, out commandNumber, out rotationBytes );

		if ( snapshot.Lookup.TryGetValue( SnapshotScaleSlot, out var scaleEntry ) )
			TryParseTransformSlot( scaleEntry.Value, out hostDirect, out commandNumber, out scaleBytes );

		if ( hostDirect )
		{
			_transformHistory.Clear();
			ApplyTransformBytes( positionBytes, rotationBytes, scaleBytes, snapshot );
			return;
		}

		var authoritativeBytes = SerializeTransformBytes();
		var receivedBytes = CombineTransformBytes( positionBytes, rotationBytes, scaleBytes );

		if ( receivedBytes is not null && PredictionSnapshotEncoding.Hash( receivedBytes ) == PredictionSnapshotEncoding.Hash( authoritativeBytes ) )
		{
			_transformHistory.Truncate( commandNumber );
			return;
		}

		Log.Warning(
			$"Prediction correction on {GameObject.Name}: Transform " +
			$"(cmd {commandNumber}) authoritative overwritten predicted state" );

		_transformHistory.Truncate( commandNumber );
		ApplyTransformBytes( positionBytes, rotationBytes, scaleBytes, snapshot );
		_transformHistory.Replay( bytes => ApplyTransformHistoryBytes( bytes ) );
	}

	static bool TryParseTransformSlot( byte[] wrapped, out bool hostDirect, out uint commandNumber, out byte[] value )
	{
		if ( PredictionSnapshotEncoding.TryUnwrap( wrapped, out hostDirect, out commandNumber, out value ) )
			return true;

		value = wrapped;
		return wrapped is { Length: > 0 };
	}

	byte[] SerializeTransformBytes()
	{
		var flags = GameObject.Network.Flags;
		var tx = GameObject.Transform.TargetLocal;

		using var writer = ByteStream.Create( 128 );
		writer.Write( (flags & NetworkFlags.NoPositionSync) == 0 );
		writer.Write( (flags & NetworkFlags.NoRotationSync) == 0 );
		writer.Write( (flags & NetworkFlags.NoScaleSync) == 0 );

		if ( (flags & NetworkFlags.NoPositionSync) == 0 )
			writer.Write( tx.Position );

		if ( (flags & NetworkFlags.NoRotationSync) == 0 )
			writer.Write( tx.Rotation );

		if ( (flags & NetworkFlags.NoScaleSync) == 0 )
			writer.Write( tx.Scale );

		return writer.ToArray();
	}

	byte[] ReadTransformBytesFromSnapshot( DeltaSnapshot snapshot )
	{
		var flags = GameObject.Network.Flags;
		using var writer = ByteStream.Create( 128 );

		var hasPosition = (flags & NetworkFlags.NoPositionSync) == 0;
		var hasRotation = (flags & NetworkFlags.NoRotationSync) == 0;
		var hasScale = (flags & NetworkFlags.NoScaleSync) == 0;

		writer.Write( hasPosition );
		writer.Write( hasRotation );
		writer.Write( hasScale );

		if ( hasPosition && snapshot.TryGetValue<Vector3>( SnapshotPositionSlot, out var position ) )
			writer.Write( position );
		else if ( hasPosition )
			return null;

		if ( hasRotation && snapshot.TryGetValue<Rotation>( SnapshotRotationSlot, out var rotation ) )
			writer.Write( rotation );
		else if ( hasRotation )
			return null;

		if ( hasScale && snapshot.TryGetValue<Vector3>( SnapshotScaleSlot, out var scale ) )
			writer.Write( scale );
		else if ( hasScale )
			return null;

		return writer.ToArray();
	}

	static byte[] CombineTransformBytes( byte[] positionBytes, byte[] rotationBytes, byte[] scaleBytes )
	{
		if ( positionBytes is null && rotationBytes is null && scaleBytes is null )
			return null;

		using var writer = ByteStream.Create( 128 );
		writer.Write( positionBytes is not null );
		writer.Write( rotationBytes is not null );
		writer.Write( scaleBytes is not null );

		if ( positionBytes is not null )
		{
			var reader = ByteStream.CreateReader( positionBytes );
			writer.Write( reader.Read<Vector3>() );
			reader.Dispose();
		}

		if ( rotationBytes is not null )
		{
			var reader = ByteStream.CreateReader( rotationBytes );
			writer.Write( reader.Read<Rotation>() );
			reader.Dispose();
		}

		if ( scaleBytes is not null )
		{
			var reader = ByteStream.CreateReader( scaleBytes );
			writer.Write( reader.Read<Vector3>() );
			reader.Dispose();
		}

		return writer.ToArray();
	}

	void ApplyTransformBytes( byte[] positionBytes, byte[] rotationBytes, byte[] scaleBytes, DeltaSnapshot snapshot )
	{
		snapshot.TryGetValue<bool>( SnapshotInterpolationSlot, out var clearInterpolation );

		var didTransformChange = false;
		var transform = GameObject.Transform.TargetLocal;

		if ( positionBytes is not null )
		{
			var reader = ByteStream.CreateReader( positionBytes );
			transform.Position = reader.Read<Vector3>();
			reader.Dispose();
			didTransformChange = true;
		}

		if ( rotationBytes is not null )
		{
			var reader = ByteStream.CreateReader( rotationBytes );
			transform.Rotation = reader.Read<Rotation>();
			reader.Dispose();
			didTransformChange = true;
		}

		if ( scaleBytes is not null )
		{
			var reader = ByteStream.CreateReader( scaleBytes );
			transform.Scale = reader.Read<Vector3>();
			reader.Dispose();
			didTransformChange = true;
		}

		if ( didTransformChange )
			GameObject.Transform.FromNetwork( transform, clearInterpolation );
	}

	void ApplyTransformHistoryBytes( byte[] bytes )
	{
		var reader = ByteStream.CreateReader( bytes );
		var hasPosition = reader.Read<bool>();
		var hasRotation = reader.Read<bool>();
		var hasScale = reader.Read<bool>();

		var transform = GameObject.Transform.TargetLocal;

		if ( hasPosition )
			transform.Position = reader.Read<Vector3>();

		if ( hasRotation )
			transform.Rotation = reader.Read<Rotation>();

		if ( hasScale )
			transform.Scale = reader.Read<Vector3>();

		reader.Dispose();
		GameObject.Transform.FromNetwork( transform, false );
	}

	internal void WriteSnapshotTransform( LocalSnapshotState snapshot, NetworkFlags flags )
	{
		var writeTransform = !IsProxy
			|| (Networking.IsHost && HasPredictTransform);

		if ( !writeTransform )
			return;

		var tx = GameObject.Transform.TargetLocal;

		if ( (flags & NetworkFlags.NoPositionSync) == 0 )
			WriteTransformSlot( snapshot, SnapshotPositionSlot, tx.Position, LocalSnapshotState.HashFlags.All );

		if ( (flags & NetworkFlags.NoRotationSync) == 0 )
			WriteTransformSlot( snapshot, SnapshotRotationSlot, tx.Rotation, LocalSnapshotState.HashFlags.All );

		if ( (flags & NetworkFlags.NoScaleSync) == 0 )
			WriteTransformSlot( snapshot, SnapshotScaleSlot, tx.Scale, LocalSnapshotState.HashFlags.All );

		LocalSnapshotState.AddCached( _snapshotCache, SnapshotInterpolationSlot, _clearInterpolationFlag );
		LocalSnapshotState.AddCached( _snapshotCache, SnapshotEnabledSlot, GameObject.Enabled );
	}

	void WriteTransformSlot<T>( LocalSnapshotState snapshot, int slot, in T value, LocalSnapshotState.HashFlags hashFlags )
	{
		var cached = _snapshotCache.GetCached( slot, value, out _ );
		var bytes = cached;

		if ( Networking.IsHost && HasPredictTransform )
		{
			var hostDirect = CurrentWriteSource == PredictionWriteSource.HostDirect;
			var commandNumber = hostDirect ? 0u : _ownerReconcileCommandNumber;
			bytes = PredictionSnapshotEncoding.Wrap( cached, hostDirect, commandNumber );
		}

		snapshot.AddSerialized( slot, bytes, hashFlags );
	}
}
