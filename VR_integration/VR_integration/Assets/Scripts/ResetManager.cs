using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class RestartManager : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Thời gian chờ trước khi restart để đảm bảo các hệ thống đã dọn dẹp xong")]
    public float restartDelay = 0.5f;

    /// <summary>
    /// Hàm chính để thực hiện Restart dự án.
    /// Có thể gọi hàm này từ sự kiện OnClick của Button hoặc XR Interactable.
    /// </summary>
    public void RestartProject()
    {
        Debug.Log("<color=yellow>[RestartManager]</color> Restart sequence initiated...");
        StartCoroutine(RestartCoroutine());
    }

    private IEnumerator RestartCoroutine()
    {
        // 1. Thông báo trạng thái hiện tại
        string sceneName = SceneManager.GetActiveScene().name;
        Debug.Log($"[RestartManager] Cleaning up scene: {sceneName}");

        // 2. Tạm dừng một chút để người dùng không bị chóng mặt (nếu có hiệu ứng Fade thì tốt hơn)
        yield return new WaitForSeconds(restartDelay);

        // 3. Thực hiện tải lại Scene
        // Lưu ý: Khi Scene tải lại, các script UCRenderer sẽ tự động gọi Dispose()
        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName);

        while (!asyncLoad.isDone)
        {
            float progress = Mathf.Clamp01(asyncLoad.progress / 0.9f);
            if (progress % 0.2f == 0) // Log tiến trình mỗi 20%
                Debug.Log($"[RestartManager] Reloading progress: {progress * 100}%");
            yield return null;
        }

        Debug.Log("<color=green>[RestartManager] Project Restarted Successfully.</color>");
    }

    // Phím tắt để test nhanh trên PC (nhấn phím R)
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            RestartProject();
        }
    }
}