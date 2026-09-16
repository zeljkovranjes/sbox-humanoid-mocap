using System;
using System.Linq;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class PreviewWidget
{
    ulong _headMeshMask,_savedHeadMeshBits;
    bool _firstPerson,_headMeshHidden;

    void ConfigureHeadVisibility()
    {
        // Use the exposed model body-part metadata. Never hide an arbitrary mesh or
        // move/scale bones to work around a first-person camera intersection.
        var head=_sceneModel.Model.Parts.All.FirstOrDefault(p=>string.Equals(p.Name,"Head",StringComparison.OrdinalIgnoreCase));
        if(head is null||!head.Choices.Any(c=>c.Mask==0))return;
        _headMeshMask=head.Mask;
    }

    void UpdateHeadVisibility()
    {
        if(!_sceneModel.IsValid()||_headMeshMask==0||_headMeshHidden==_firstPerson)return;
        var mask=_sceneModel.MeshGroupMask;
        if(_firstPerson)
        {
            _savedHeadMeshBits=mask&_headMeshMask;
            _sceneModel.MeshGroupMask=mask&~_headMeshMask;
        }
        else _sceneModel.MeshGroupMask=(mask&~_headMeshMask)|_savedHeadMeshBits;
        _headMeshHidden=_firstPerson;
    }

    public bool FirstPerson
    {
        get=>_firstPerson;
        set{_firstPerson=value;UpdateHeadVisibility();}
    }
}
