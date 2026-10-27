using System;

namespace AsmdefManager;

// A reference that has been staged in memory but not written to disk yet.
[Serializable]
class PendingRef
{
    public string targetPath; // asmdef that gets the reference
    public string sourceGuid; // asmdef being referenced
}