using System.Numerics;
namespace HumanoidMocap.Inference;
using Vector3 = System.Numerics.Vector3;
public sealed record HandObservation(string Side,float Presence,float Handedness,
    Vector3[] ImageLandmarks,Vector3[] RelativeWorldLandmarks,bool Tracked=false);
