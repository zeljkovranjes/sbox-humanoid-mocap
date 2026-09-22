using System;
using System.IO;
using System.Security.Cryptography;

namespace HumanoidMocap.Inference;

/// <summary>SHA-256 of a file, remembered beside it. Model checkpoints run to several gigabytes and every
/// capture verified them again, twice for some, which cost seconds per job. The digest is stored in
/// <c>&lt;file&gt;.sha256</c> with the file's length and modification time and reused while both are
/// unchanged; a partial, replaced or rewritten file changes them and is hashed again.</summary>
public static class FileChecksum
{
    /// <summary>Upper-case hexadecimal SHA-256 of the file.</summary>
    public static string Sha256(string path)
    {
        var info=new FileInfo(path);if(!info.Exists)throw new FileNotFoundException("Missing file.",path);
        var stamp=$"{info.Length}|{info.LastWriteTimeUtc.Ticks}|";var sidecar=path+".sha256";
        try
        {
            if(File.Exists(sidecar)&&File.ReadAllText(sidecar) is var text&&text.StartsWith(stamp,StringComparison.Ordinal)&&text.Length==stamp.Length+64)
                return text[stamp.Length..];
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
        string digest;using(var input=File.OpenRead(path))digest=Convert.ToHexString(SHA256.HashData(input));
        // Only record the digest if the file did not change while it was read.
        info.Refresh();
        if($"{info.Length}|{info.LastWriteTimeUtc.Ticks}|"==stamp)
            try{File.WriteAllText(sidecar,stamp+digest);}catch(IOException){}catch(UnauthorizedAccessException){}
        return digest;
    }
    public static bool Matches(string path,string sha256)=>Sha256(path).Equals(sha256,StringComparison.OrdinalIgnoreCase);
}
