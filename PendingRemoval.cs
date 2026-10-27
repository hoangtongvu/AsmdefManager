using System;

namespace AsmdefManager;

// A reference that exists on disk and is staged for removal.
// 'reference' is the raw string stored in the asmdef ("GUID:xxxx" or an assembly name).
[Serializable]
class PendingRemoval
{
    public string targetPath;
    public string reference;
}