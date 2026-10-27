using System;
using System.Collections.Generic;

namespace AsmdefManager;

// Mirrors the .asmdef JSON schema so we can round-trip it with JsonUtility.
[Serializable]
class AsmdefData
{
    public string name;
    public string rootNamespace;
    public string[] references = new string[0];
    public string[] includePlatforms = new string[0];
    public string[] excludePlatforms = new string[0];
    public bool allowUnsafeCode;
    public bool overrideReferences;
    public string[] precompiledReferences = new string[0];
    public bool autoReferenced = true;
    public string[] defineConstraints = new string[0];
    public List<VersionDefine> versionDefines = new List<VersionDefine>();
    public bool noEngineReferences;
}