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

    // Biến cho Thí nghiệm (Total Average FPS)
    private bool isRecording = false;
    private float sessionAccum = 0f;
    private int sessionFrames = 0;

    void Start()
    {
        timeleft = updateInterval;
    }

    void Update()
    {
        // 1. Logic cho UI (giữ nguyên để bạn nhìn thấy số trên màn hình)
        timeleft -= Time.deltaTime;
        accum += Time.timeScale / Time.deltaTime;
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

        // 2. Logic tính Average FPS cho cả phiên thí nghiệm
        if (isRecording)
        {
            // Cộng dồn để tính trung bình cộng thực sự
            sessionAccum += Time.timeScale / Time.deltaTime;
            sessionFrames++;
        }

        // 3. Phím tắt để Start/Stop thí nghiệm
        if (Input.GetKeyDown(KeyCode.X)) // Nhấn phím X để Bắt đầu/Kết thúc
        {
            if (!isRecording) 
            {
                // Bắt đầu
                isRecording = true;
                sessionAccum = 0f;
                sessionFrames = 0;
                Debug.Log("--- BẮT ĐẦU GHI FPS TRUNG BÌNH ---");
            }
            else 
            {
                // Kết thúc và In ra kết quả
                isRecording = false;
                float avgFPS = sessionAccum / sessionFrames;
                Debug.Log(">> KẾT QUẢ THÍ NGHIỆM: FPS TRUNG BÌNH = " + avgFPS.ToString("F2") + " FPS");
            }
        }
    }
}