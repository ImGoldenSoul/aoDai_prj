using UnityEngine;
using TMPro; // Nếu bạn dùng TextMeshPro

public class FPSCounter : MonoBehaviour
{
    public TextMeshProUGUI fpsText; // Kéo thả UI Text vào đây
    public float updateInterval = 0.5f; // Thời gian làm mới con số (giây)

    private float accum = 0; 
    private int frames = 0; 
    private float timeleft; 

    void Start()
    {
        timeleft = updateInterval;
    }

    void Update()
    {
        timeleft -= Time.deltaTime;
        accum += Time.timeScale / Time.deltaTime;
        ++frames;

        // Khi hết khoảng thời gian interval thì cập nhật UI
        if (timeleft <= 0.0)
        {
            float fps = accum / frames;
            string format = string.Format("{0:F2} FPS", fps);
            fpsText.text = format;

            // Đổi màu text dựa trên hiệu năng
            if (fps < 30) fpsText.color = Color.red;
            else if (fps < 60) fpsText.color = Color.yellow;
            else fpsText.color = Color.green;

            // Reset các thông số
            timeleft = updateInterval;
            accum = 0.0f;
            frames = 0;
        }
    }
}