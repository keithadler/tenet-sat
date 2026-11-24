using System.IO.MemoryMappedFiles;

namespace TenetSat;

/// <summary>Read-only memory mapping of a whole file. An empty file maps to length 0.</summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _ptr;

    public byte* Ptr => _ptr;
    public long Length { get; }

    public MappedFile(string path)
    {
        // The length comes from the open file: FileInfo.Length on a symbolic link is the link's own size.
        var handle = File.OpenHandle(path);
        Length = RandomAccess.GetLength(handle);
        if (Length == 0) { handle.Dispose(); return; }
        _mmf = MemoryMappedFile.CreateFromFile(handle, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        _view = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        _ptr += _view.PointerOffset;
    }

    public void Dispose()
    {
        if (_view is not null)
        {
            if (_ptr != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
            _view.Dispose();
        }
        _mmf?.Dispose();
    }
}
