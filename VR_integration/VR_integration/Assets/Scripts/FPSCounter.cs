using UnityEngine;
using TMPro;

public class FPSCounter : MonoBehaviour
{
    public TextMeshProUGUI fpsText;
    public float updateInterval = 0.5f;

    // Biến cho UI (Rolling FPS)
    private float accum = 0;
    private int frames = 0;
    private float timeleft;

    // Biến cho Thí nghiệm (Total Average FPS chuẩn)
    private bool isRecording = false;
    private float sessionTotalTime = 0f; // Tổng thời gian chạy thí nghiệm
    private int sessionFrames = 0;        // Tổng số frame bắt được

    void Start()
    {
        timeleft = updateInterval;
    }

    void Update()
    {
        // Lấy thời gian thực của frame hiện tại (bất chấp timeScale)
        float dt = Time.unscaledDeltaTime;

        // 1. Logic cho UI
        timeleft -= dt;
        accum += 1.0f / dt;
        ++frames;

        if (timeleft <= 0.0)
        {
            float fps = accum / frames;
            fpsText.text = string.Format("{0:F2} FPS", fps);
            
            // Reset UI
            timeleft = updateInterval;
            accum = 0.0f;
            frames = 0;
        }

        // 2. Logic tính Average FPS cho cả phiên thí nghiệm (Chuẩn khoa học)
        if (isRecording)
        {
            sessionTotalTime += dt; // Cộng dồn tổng thời gian thực
            sessionFrames++;        // Cộng dồn tổng số khung hình
        }

        // 3. Phím tắt để Start/Stop thí nghiệm
        if (Input.GetKeyDown(KeyCode.X)) 
        {
            if (!isRecording) 
            {
                isRecording = true;
                sessionTotalTime = 0f;
                sessionFrames = 0;
                Debug.Log("--- BẮT ĐẦU GHI FPS TRUNG BÌNH ---");
            }
            else 
            {
                isRecording = false;
                
                // Tránh lỗi chia cho 0 nếu tắt quá nhanh
                float avgFPS = (sessionTotalTime > 0) ? (sessionFrames / sessionTotalTime) : 0f;
                
                Debug.Log($">> KẾT QUẢ THÍ NGHIỆM: Tổng thời gian = {sessionTotalTime:F2}s | Tổng số Frames = {sessionFrames}");
                Debug.Log($">> FPS TRUNG BÌNH THỰC TẾ = {avgFPS:F2} FPS");
            }
        }
    }
}