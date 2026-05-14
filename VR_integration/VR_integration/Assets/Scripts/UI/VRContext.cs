using UnityEngine;

public class VRContext : MonoBehaviour
{
    public static VRContext Instance;

    [Header("Global VR References")]
    public Transform leftHandController;
    public Transform grabSphereTarget;

    [Header("Môi trường (Sàn, Bàn...)")]
    [Tooltip("Kéo Box Collider của mặt bàn, sàn nhà vào đây")]
    public BoxCollider[] environmentColliders;

    void Awake()
    {
        Instance = this;
    }
}