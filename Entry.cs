namespace AsmdefManager;

class Entry
{
    public string Name;
    public string Path;      // asset path, e.g. Assets/Foo/Foo.asmdef
    public string FullPath;  // absolute file path on disk
    public string Guid;
    public AsmdefData Data;
}