using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AIEngineer.AutoCAD
{
    internal sealed class DrawingDeltaSnapshot
    {
        public readonly string BaselineIndexId, BaselineIndexSha256, BaselineSourceSha256;
        public readonly string[] AddedHandles, ModifiedHandles, RemovedHandles;
        public readonly int BaselineEntityCount;

        public DrawingDeltaSnapshot(
            string indexId, string indexSha, string sourceSha, int baselineEntityCount,
            string[] added, string[] modified, string[] removed)
        {
            BaselineIndexId = indexId; BaselineIndexSha256 = indexSha;
            BaselineSourceSha256 = sourceSha; BaselineEntityCount = baselineEntityCount;
            AddedHandles = added; ModifiedHandles = modified; RemovedHandles = removed;
        }
    }

    internal static class DrawingChangeTracker
    {
        private static readonly object Sync = new object();
        private static Database _database;
        private static IntPtr _databasePointer;
        private static string _indexId, _indexSha, _sourceSha;
        private static int _baselineEntityCount;
        private static HashSet<string> _baseline, _added, _modified, _removed;

        public static void SetBaseline(
            Document document, string indexId, string indexSha, string sourceSha,
            IEnumerable<ObjectId> identifiers)
        {
            lock (Sync)
            {
                Detach();
                _database = document.Database;
                _databasePointer = document.Database.UnmanagedObject;
                _indexId = indexId; _indexSha = indexSha; _sourceSha = sourceSha;
                _baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId identifier in identifiers)
                    if (!identifier.IsNull && identifier.IsValid && !identifier.IsErased)
                        _baseline.Add(identifier.Handle.ToString());
                _baselineEntityCount = _baseline.Count;
                _added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _database.ObjectAppended += OnAppended;
                _database.ObjectModified += OnModified;
                _database.ObjectErased += OnErased;
            }
        }

        public static DrawingDeltaSnapshot Snapshot(Document document)
        {
            lock (Sync)
            {
                if (_database == null || _databasePointer != document.Database.UnmanagedObject ||
                    String.IsNullOrEmpty(_indexId))
                    throw new InvalidOperationException(
                        "No in-session drawing-index baseline is armed; run a complete index first.");
                return new DrawingDeltaSnapshot(
                    _indexId, _indexSha, _sourceSha, _baselineEntityCount,
                    Sorted(_added), Sorted(_modified), Sorted(_removed));
            }
        }

        public static void Complete(Document document)
        {
            lock (Sync)
            {
                if (_database == null || _databasePointer != document.Database.UnmanagedObject)
                    throw new InvalidOperationException("Drawing delta baseline changed during capture.");
                Detach();
            }
        }

        public static void Stop()
        { lock (Sync) Detach(); }

        private static void OnAppended(object sender, ObjectEventArgs arguments)
        {
            Entity entity = arguments.DBObject as Entity;
            if (entity == null) return;
            lock (Sync)
            {
                string handle = entity.Handle.ToString();
                _removed.Remove(handle); _modified.Remove(handle); _added.Add(handle);
            }
        }

        private static void OnModified(object sender, ObjectEventArgs arguments)
        {
            Entity entity = arguments.DBObject as Entity;
            if (entity == null) return;
            lock (Sync)
            {
                string handle = entity.Handle.ToString();
                if (!_added.Contains(handle) && !_removed.Contains(handle))
                    _modified.Add(handle);
            }
        }

        private static void OnErased(object sender, ObjectErasedEventArgs arguments)
        {
            Entity entity = arguments.DBObject as Entity;
            if (entity == null) return;
            lock (Sync)
            {
                string handle = entity.Handle.ToString();
                if (arguments.Erased)
                {
                    if (!_added.Remove(handle)) _removed.Add(handle);
                    _modified.Remove(handle);
                }
                else
                {
                    _removed.Remove(handle);
                    if (_baseline.Contains(handle)) _modified.Add(handle);
                    else _added.Add(handle);
                }
            }
        }

        private static string[] Sorted(HashSet<string> values)
        {
            string[] result = new string[values.Count]; values.CopyTo(result);
            Array.Sort(result, delegate(string left, string right)
            {
                long leftValue = Convert.ToInt64(left, 16);
                long rightValue = Convert.ToInt64(right, 16);
                return leftValue.CompareTo(rightValue);
            });
            return result;
        }

        private static void Detach()
        {
            if (_database != null)
            {
                _database.ObjectAppended -= OnAppended;
                _database.ObjectModified -= OnModified;
                _database.ObjectErased -= OnErased;
            }
            _database = null; _indexId = null; _indexSha = null; _sourceSha = null;
            _databasePointer = IntPtr.Zero;
            _baselineEntityCount = 0; _baseline = null; _added = null;
            _modified = null; _removed = null;
        }
    }
}
