using UnityEngine;
using System.Collections.Generic;

public class FabricSpawnerUI : MonoBehaviour
{
    [Header("Cài đặt Prefab & Vị trí")]
    public GameObject fabricPrefab;
    public Transform spawnPoint;

    [Header("Cài đặt Spawn liên tiếp")]
    public Vector3 spawnOffset = new Vector3(0.5f, 0, 0); 
    private int spawnCount = 0;

    [Header("Quản lý bộ nhớ Spawn")]
    [Tooltip("Danh sách tự động lưu các miếng vải đã tạo")]
    public List<GameObject> spawnedFabrics = new List<GameObject>(); 

    [Header("Cài đặt Quản lý")]
    public string containerTag = "FabricContainer";
    public CuttingManager_UCloth cuttingManager;

    public void SpawnNewFabric()
    {
        if (fabricPrefab == null) return;

        // 1. Tính toán vị trí mới
        Vector3 basePos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        Vector3 finalPos = basePos + (spawnOffset * spawnCount);
        Quaternion rot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;
        
        // 2. Sinh ra tổ hợp mới
        GameObject newFabricGroup = Instantiate(fabricPrefab, finalPos, rot);
        newFabricGroup.tag = containerTag;

        // BƯỚC MỚI: Thêm miếng vải vừa tạo vào danh sách trí nhớ
        spawnedFabrics.Add(newFabricGroup);

        // Tăng bộ đếm
        spawnCount++;

        // 3. Tự động kế thừa giá trị từ VRContext
        if (VRContext.Instance == null)
        {
            Debug.LogError("Chưa tìm thấy VRContext trong Scene!");
            return;
        }

        UCloth.UCCloth[] uCloths = newFabricGroup.GetComponentsInChildren<UCloth.UCCloth>();
        
        foreach (var clothObj in uCloths)
        {
            UClothLaserGrabber grabber = clothObj.GetComponent<UClothLaserGrabber>();
            if (grabber != null)
            {
                grabber.vrController = VRContext.Instance.leftHandController; 
                grabber.grabSphere = VRContext.Instance.grabSphereTarget;
            }

            clothObj.gameObject.tag = "Cloth"; 
            if (cuttingManager != null)
            {
                cuttingManager.RegisterClothObject(clothObj.gameObject);
            }
            
            Debug.Log($"[FabricSpawnerUI] Spawn miếng vải số {spawnCount} thành công!");
        }
    }

    // ==========================================
    // HÀM: DÀNH CHO NÚT DELETE (Xóa 1 cái gần nhất)
    // ==========================================
    public void DeleteLastFabric()
    {
        if (spawnedFabrics.Count > 0)
        {
            int lastIndex = spawnedFabrics.Count - 1;
            GameObject lastFabric = spawnedFabrics[lastIndex];

            if (lastFabric != null)
            {
                Destroy(lastFabric);
            }

            spawnedFabrics.RemoveAt(lastIndex);

            if (spawnCount > 0)
            {
                spawnCount--;
            }

            Debug.Log("[FabricSpawnerUI] Đã xóa miếng vải gần nhất!");
        }
        else
        {
            Debug.LogWarning("[FabricSpawnerUI] Không còn miếng vải nào để xóa nữa!");
        }
    }

    // ==========================================
    // HÀM MỚI: DÀNH CHO NÚT RESTART (Xóa tất cả)
    // ==========================================
    public void Restart()
    {
        // 1. Duyệt qua toàn bộ danh sách và tiêu hủy từng Game Object
        foreach (GameObject fabric in spawnedFabrics)
        {
            if (fabric != null)
            {
                Destroy(fabric);
            }
        }

        // 2. Dọn sạch danh sách trí nhớ
        spawnedFabrics.Clear();

        // 3. Reset bộ đếm vị trí về 0 để bắt đầu lại từ đầu
        spawnCount = 0;

        Debug.Log("[FabricSpawnerUI] Đã Restart: Xóa toàn bộ vải và reset vị trí!");
    }
}