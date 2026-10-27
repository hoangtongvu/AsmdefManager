namespace AsmdefManager;

// Lightweight info about EVERY asmdef in the project (Assets + all packages),
// used to categorize and sort references.
internal sealed class AsmInfo
{
    public string Name;
    public string Guid;
    public RefCategory Category;
}