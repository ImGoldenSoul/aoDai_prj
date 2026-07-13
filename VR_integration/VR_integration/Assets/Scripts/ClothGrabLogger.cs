using UnityEngine;
using System.IO;
using System;

public class ClothGrabLogger : MonoBehaviour
{
    [Header("Cấu hình Thử nghiệm")]
    [Tooltip("Mật độ lưới vải hiện tại đang test")]
    public string currentResolution = "10x10";
    public int maxTrialsPerParticipant = 5;
    
    [Tooltip("Ngưỡng khoảng cách sai số tối đa để tính là Success (đơn vị: cm)")]
    public float successThresholdCm = 3.0f;
    
    [Tooltip("Khoảng cách tối đa (mét) để hệ thống nhận diện thả vải vào đích. Thả xa hơn tính là tuột tay.")]
    public float dropIntentRadius = 0.2f;

    [Header("Tham chiếu Target")]
    [Tooltip("Vị trí đích cần đưa tới (Vòng Tròn Xanh)")]
    public Transform targetPoint;

    [Header("Target Randomizer (Tự động di chuyển đích)")]
    [Tooltip("Bật để tự động đổi vị trí target sau mỗi lượt thử")]
    public bool randomizeTarget = true;
    
    [Tooltip("Vị trí gốc (tâm vùng spawn). Phải đặt mốc này nằm trên mặt phẳng sát với miếng vải.")]
    public Transform targetAnchor;
    
    [Tooltip("Bán kính tối đa (mét) mà target có thể nảy ra từ Anchor. Ví dụ: 0.2 = 20cm")]
    public float spawnRadius = 0.2f;
    
    [Tooltip("Bật lên nếu vải đang treo dọc (mặt phẳng XY). Target sẽ chỉ nảy lên, xuống, trái, phải trên mặt phẳng đó, không lún sâu theo trục Z.")]
    public bool lockZAxis = true;

    // --- Trạng thái nội bộ ---
    private string csvFilePath;
    private int currentParticipantID = 1;
    private int currentTrialIndex = 1;
    
    public bool IsTrialActive { get; private set; } = false;
    
    private float grabStartTime;
    private int regrabCount = 0;
    private float fpsAccumulator = 0f;
    private int fpsFrameCount = 0;

    void Start()
    {
        string folderPath = @"C:\Users\toikh\Documents\3D_Museum\aoDai_prj\VR_integration\UserStudy";
        
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            Debug.Log($"[ClothGrabLogger] Đã tự động tạo thư mục: {folderPath}");
        }

        csvFilePath = Path.Combine(folderPath, "ClothGrab_Experiment_RawData.csv");
        EnsureCsvHeaderAndResumeState();
        
        // --- DI CHUYỂN TARGET NGAY KHI VỪA PLAY GAME ---
        MoveTargetToRandomPosition();
    }

    void Update()
    {
        if (IsTrialActive)
        {
            float currentFPS = 1.0f / Time.unscaledDeltaTime;
            fpsAccumulator += currentFPS;
            fpsFrameCount++;
        }
    }

    private void EnsureCsvHeaderAndResumeState()
    {
        if (!File.Exists(csvFilePath))
        {
            string header = "Participant,Trial,Resolution,Start Time,End Time,Success,Final Error (cm),Regrab Count,FPS Avg\n";
            File.WriteAllText(csvFilePath, header);
            currentParticipantID = 1;
            currentTrialIndex = 1;
            Debug.Log($"[ClothGrabLogger] Đã tạo file CSV mới tại: {csvFilePath}. Bắt đầu từ P01.");
        }
        else
        {
            try
            {
                string[] lines = File.ReadAllLines(csvFilePath);
                if (lines.Length > 1) 
                {
                    string lastLine = "";
                    for (int i = lines.Length - 1; i >= 1; i--)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                        {
                            lastLine = lines[i];
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(lastLine))
                    {
                        string[] columns = lastLine.Split(',');
                        if (columns.Length >= 2)
                        {
                            string pString = columns[0].Replace("P", "");
                            if (int.TryParse(pString, out int lastP) && int.TryParse(columns[1], out int lastTrial))
                            {
                                currentParticipantID = lastP;
                                currentTrialIndex = lastTrial + 1;

                                if (currentTrialIndex > maxTrialsPerParticipant)
                                {
                                    currentTrialIndex = 1;
                                    currentParticipantID++;
                                }
                                
                                Debug.Log($"[ClothGrabLogger] Tìm thấy file cũ. Tự động nối tiếp dữ liệu. Hiện tại là: P{currentParticipantID:D2}, Trial: {currentTrialIndex}");
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ClothGrabLogger] Lỗi khi đọc file cũ: {e.Message}. Sẽ log lại từ P01.");
            }
        }
    }

    public void NotifyGrabStarted()
    {
        if (!IsTrialActive)
        {
            grabStartTime = Time.time;
            regrabCount = 0;
            fpsAccumulator = 0f;
            fpsFrameCount = 0;
            IsTrialActive = true;
            Debug.Log($"<color=green>[BẮT ĐẦU TRIAL]</color> Participant: P{currentParticipantID:D2}, Trial: {currentTrialIndex}");
        }
        else
        {
            regrabCount++;
            Debug.Log($"[LOG] Regrab detected. Tổng số lần bắt lại: {regrabCount}");
        }
    }

    public void NotifyClothDropped(Vector3 dropPosition)
    {
        if (!IsTrialActive || targetPoint == null) return;

        float distToTargetMeters = Vector3.Distance(dropPosition, targetPoint.position);

        if (distToTargetMeters <= dropIntentRadius)
        {
            EndTrial(distToTargetMeters);
        }
        else
        {
            Debug.Log($"[LOG] Tuột tay ngoài vùng đích (Cách đích: {distToTargetMeters:F2}m). Đợi bám lại...");
        }
    }

    private void EndTrial(float finalDistanceMeters)
    {
        float grabEndTime = Time.time;
        IsTrialActive = false;

        float errorDistanceCm = finalDistanceMeters * 100f; 
        int successResult = (errorDistanceCm <= successThresholdCm) ? 1 : 0;
        float averageFps = fpsFrameCount > 0 ? (fpsAccumulator / fpsFrameCount) : 90f;

        WriteTrialToCsv(currentParticipantID, currentTrialIndex, currentResolution, grabStartTime, grabEndTime, successResult, errorDistanceCm, regrabCount, averageFps);

        Debug.Log($"<color=yellow>[KẾT THÚC TRIAL {currentTrialIndex}]</color> Error: {errorDistanceCm:F2}cm | Success: {successResult} | FPS: {averageFps:F1}");

        AdvanceExperimentProgression();
        
        // --- DI CHUYỂN TARGET NGAY SAU KHI THẢ THÀNH CÔNG ---
        MoveTargetToRandomPosition();
    }

    private void WriteTrialToCsv(int participant, int trial, string res, float start, float end, int success, float error, int regrab, float fps)
    {
        string participantStr = $"P{participant:D2}";
        string csvLine = $"{participantStr},{trial},{res},{start:F2},{end:F2},{success},{error:F2},{regrab},{fps:F1}\n";
        
        try
        {
            File.AppendAllText(csvFilePath, csvLine);
        }
        catch (IOException ex)
        {
            Debug.LogError($"[LỖI GHI FILE CSV] {ex.Message}");
        }
    }

    private void AdvanceExperimentProgression()
    {
        currentTrialIndex++;
        if (currentTrialIndex > maxTrialsPerParticipant)
        {
            currentTrialIndex = 1;
            currentParticipantID++;
            Debug.Log($"<color=cyan>[HỆ THỐNG]</color> Đã đủ {maxTrialsPerParticipant} lượt. Chuyển sang Participant tiếp theo: P{currentParticipantID:D2}");
        }
    }

    /// <summary>
    /// THUẬT TOÁN DI CHUYỂN ĐÍCH ĐẾN
    /// </summary>
    private void MoveTargetToRandomPosition()
    {
        // Kiểm tra xem đã bật Randomize chưa và đã kéo thả các object vào chưa
        if (!randomizeTarget || targetPoint == null || targetAnchor == null) return;

        // B1: Lấy một vị trí ngẫu nhiên nằm trong một hình cầu vô hình (Bán kính = spawnRadius)
        Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * spawnRadius;
        
        // B2: Khóa trục Z (Ép phẳng)
        // Việc này ngăn target bị chui lún ra đằng sau lưng miếng vải khiến người chơi không kéo tới được.
        if (lockZAxis) 
        {
            randomOffset.z = 0; 
        }

        // B3: Đẩy vòng tròn xanh tới vị trí mới = Điểm Anchor Gốc + Lệch Ngẫu Nhiên
        targetPoint.position = targetAnchor.position + randomOffset;
        
        Debug.Log($"[Target] Đã đổi vị trí đích thành công.");
    }
}