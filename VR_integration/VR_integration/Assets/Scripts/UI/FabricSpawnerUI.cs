using UnityEngine;

public class FabricSpawnerUI : MonoBehaviour
{
    [Header("Cài đặt Prefab & Vị trí")]
    public GameObject fabricPrefab;
    public Transform spawnPoint;

    [Header("Cài đặt Quản lý")]
    public string containerTag = "FabricContainer";
    public CuttingManager_UCloth cuttingManager;

    [Header("Cầu nối VR cho Prefab mới")]
    [Tooltip("Kéo Right Controller từ Scene vào đây")]
    public Transform rightHandController;
    
    [Tooltip("Kéo quả cầu Grab Sphere (mục tiêu đỏ) vào đây")]
    public Transform grabSphereTarget;

    public void SpawnNewFabric()
    {
        if (fabricPrefab == null) return;

        // 1. Dọn dẹp tổ hợp cũ
        GameObject[] oldContainers = GameObject.FindGameObjectsWithTag(containerTag);
        foreach (GameObject oldObj in oldContainers)
        {
            Destroy(oldObj);
        }

        // 2. Sinh ra tổ hợp mới tại điểm Spawn
        Vector3 pos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        Quaternion rot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;
        GameObject newFabricGroup = Instantiate(fabricPrefab, pos, rot);
        newFabricGroup.tag = containerTag;

        // 3. TIÊM DỮ LIỆU: Phục hồi trí nhớ cho tấm vải mới
        if (cuttingManager != null)
        {
            UCloth.UCCloth[] uCloths = newFabricGroup.GetComponentsInChildren<UCloth.UCCloth>();
            
            foreach (var clothObj in uCloths)
            {
                // -- A. Phục hồi tính năng Cầm Nắm (Grab) --
                UClothLaserGrabber grabber = clothObj.GetComponent<UClothLaserGrabber>();
                if (grabber != null)
                {
                    grabber.vrController = rightHandController;
                    grabber.grabSphere = grabSphereTarget;
                    // (Script UClothPinner sẽ tự động tìm thấy Grabber, không cần gán thêm)
                }

                // -- B. Phục hồi tính năng Cắt (Cut) --
                // Đảm bảo tấm vải có tag đúng trước khi đưa vào máy cắt
                clothObj.gameObject.tag = "Cloth"; 
                cuttingManager.RegisterClothObject(clothObj.gameObject);
                
                Debug.Log($"[FabricSpawnerUI] Đã phục hồi toàn bộ tương tác VR cho: {clothObj.gameObject.name}");
            }
        }
    }
}