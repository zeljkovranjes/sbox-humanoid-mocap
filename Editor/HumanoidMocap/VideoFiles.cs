using System;
using System.IO;
using System.Security.Cryptography;

namespace HumanoidMocap.Editor;

/// <summary>Copies source footage into the editor's local preview filesystem without changing the original.</summary>
public static class VideoFiles
{
    public static string CacheVideo(string absolutePath)
    {
        using var input=File.OpenRead(absolutePath);
        var hash=Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var relative="humanoid_mocap/video/"+hash+Path.GetExtension(absolutePath).ToLowerInvariant();
        var fs=global::Editor.FileSystem.ProjectTemporary;
        fs.CreateDirectory("humanoid_mocap/video");
        var destination=fs.GetFullPath(relative);
        if(!File.Exists(destination))File.Copy(absolutePath,destination);
        return relative;
    }
}
