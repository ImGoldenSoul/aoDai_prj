using UnityEngine;

public class VRContext : MonoBehaviour
{
    public static VRContext Instance;

    [Header("Global VR References")]
    public Transform leftHandController;
    public Transform grabSphereTarget;

    void Awake()
    {
        Instance = this;
    }
}