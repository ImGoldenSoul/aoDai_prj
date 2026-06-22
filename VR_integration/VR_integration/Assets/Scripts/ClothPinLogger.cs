using UnityEngine;
using System.IO;
using System;
using System.Collections.Generic;

public class ClothPinLogger : MonoBehaviour
{
    [Header("Cấu hình Thử nghiệm Pinning")]
    [Tooltip("Mật độ lưới vải hiện tại đang test")]
    public string currentResolution = "10x10";
    public int maxTrialsPerParticipant = 5;
    
    [Tooltip("Số lượng ghim ngẫu nhiên yêu cầu trong mỗi trial (1 đến 3)")]
    public int minPinsRequired = 1;
    public int maxPinsRequired = 3;

    [Header("Tham chiếu Pin Target")]
    [Tooltip("Prefab của vòng tròn đích (dùng để instantiate ngẫu nhiên)")]
    public GameObject targetPrefab;
    [Tooltip("Vị trí gốc (tâm vùng spawn) để sinh ra các điểm cần ghim")]
    public Transform targetAnchor;
    [Tooltip("Bán kính tối đa (mét) để sinh ra điểm ghim quanh Anchor")]
    public float spawnRadius = 0.3f;

    // --- Trạng thái nội bộ ---
    private string csvFilePath;
    private int currentParticipantID = 1;
    private int currentTrialIndex = 1;
    
    public bool IsTrialActive { get; private set; } = false;
    
    private float trialStartTime;
    private int currentPinsRequired = 0;
    private int currentPinsPlaced = 0;
    
    // Lưu trữ các target hiện tại đang hiển thị
    private List<GameObject> currentTargets = new List<GameObject>();
    // Lưu thời điểm bắt đầu thao tác ghim cho từng điểm để tính Completion Time riêng rẽ (tuỳ chọn)
    private float grabStartTime; 

    private float fpsAccumulator = 0f;
    private int fpsFrameCount = 0;

    void Start()
    {
        string folderPath = @"E:\DATN\aoDai_prj\VR_integration\UserStudy";
        
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            Debug.Log($"[ClothPinLogger] Đã tự động tạo thư mục: {folderPath}");
        }

        // Tạo một file riêng cho bài test Pinning
        csvFilePath = Path.Combine(folderPath, "ClothPin_Experiment_RawData.csv");
        EnsureCsvHeaderAndResumeState();
        
        // Bắt đầu trial đầu tiên ngay khi play
        StartNewTrial();
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
            // Các metric cho Pinning: 
            // - Pins Required: Số điểm hệ thống yêu cầu ghim trong Trial này
            // - Total Time: Thời gian hoàn thành việc ghim tất cả các điểm
            // Có thể thêm Error Distance (khoảng cách sai lệch trung bình của các điểm ghim so với target) nếu cần
            string header = "Participant,Trial,Resolution,Pins Required,Start Time,End Time,Total Time (s),FPS Avg\n";
            File.WriteAllText(csvFilePath, header);
            currentParticipantID = 1;
            currentTrialIndex = 1;
            Debug.Log($"[ClothPinLogger] Đã tạo file CSV mới tại: {csvFilePath}. Bắt đầu từ P01.");
        }
        else
        {
            // Logic đọc lại file cũ tương tự như GrabLogger
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
                                
                                Debug.Log($"[ClothPinLogger] Tìm thấy file cũ. Tự động nối tiếp dữ liệu. Hiện tại là: P{currentParticipantID:D2}, Trial: {currentTrialIndex}");
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ClothPinLogger] Lỗi khi đọc file cũ: {e.Message}. Sẽ log lại từ P01.");
            }
        }
    }

    /// <summary>
    /// Bắt đầu một Trial mới, random số lượng điểm cần ghim và spawn chúng ra
    /// </summary>
    private void StartNewTrial()
    {
        currentPinsRequired = UnityEngine.Random.Range(minPinsRequired, maxPinsRequired + 1);
        currentPinsPlaced = 0;
        
        trialStartTime = Time.time;
        fpsAccumulator = 0f;
        fpsFrameCount = 0;
        IsTrialActive = true;

        SpawnTargets(currentPinsRequired);

        Debug.Log($"<color=green>[BẮT ĐẦU PIN TRIAL]</color> Participant: P{currentParticipantID:D2}, Trial: {currentTrialIndex} | Yêu cầu ghim: {currentPinsRequired} điểm.");
    }

    private void SpawnTargets(int count)
    {
        // Xóa các target cũ (nếu có)
        foreach(var t in currentTargets)
        {
            if(t != null) Destroy(t);
        }
        currentTargets.Clear();

        if (targetPrefab == null || targetAnchor == null) return;

        for (int i = 0; i < count; i++)
        {
            Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * spawnRadius;
            randomOffset.z = 0; // Tùy chọn khóa trục Z giống Grab
            
            Vector3 spawnPos = targetAnchor.position + randomOffset;
            GameObject newTarget = Instantiate(targetPrefab, spawnPos, Quaternion.identity);
            newTarget.name = $"PinTarget_{i}";
            currentTargets.Add(newTarget);
        }
    }

    /// <summary>
    /// Được gọi từ UClothPinner khi người dùng ghim thành công một điểm
    /// </summary>
    public void NotifyPinPlaced(Vector3 pinPosition)
    {
        if (!IsTrialActive) return;

        // Tùy chọn: Ở đây bạn có thể kiểm tra xem điểm ghim có nằm gần một trong các Target không.
        // Nếu gần, tức là ghim đúng chỗ, thì ta tắt Target đó đi và tăng biến đếm.
        bool pinnedCorrectly = false;
        float tolerance = 0.05f; // Sai số 5cm

        for (int i = currentTargets.Count - 1; i >= 0; i--)
        {
            if (currentTargets[i] != null)
            {
                float dist = Vector3.Distance(pinPosition, currentTargets[i].transform.position);
                if (dist <= tolerance)
                {
                    // Ghim đúng vào target này
                    Destroy(currentTargets[i]);
                    currentTargets.RemoveAt(i);
                    currentPinsPlaced++;
                    pinnedCorrectly = true;
                    Debug.Log($"[LOG] Đã ghim trúng 1 điểm! (Cách đích: {dist*100f:F1}cm). Còn lại: {currentPinsRequired - currentPinsPlaced}");
                    break; 
                }
            }
        }

        if (!pinnedCorrectly)
        {
            Debug.Log("[LOG] Ghim sai vị trí (quá xa các target).");
        }

        // Kiểm tra xem đã ghim đủ số điểm yêu cầu chưa
        if (currentPinsPlaced >= currentPinsRequired)
        {
            EndTrial();
        }
    }

    private void EndTrial()
    {
        float trialEndTime = Time.time;
        IsTrialActive = false;

        float totalTime = trialEndTime - trialStartTime;
        float averageFps = fpsFrameCount > 0 ? (fpsAccumulator / fpsFrameCount) : 90f;

        WriteTrialToCsv(currentParticipantID, currentTrialIndex, currentResolution, currentPinsRequired, trialStartTime, trialEndTime, totalTime, averageFps);

        Debug.Log($"<color=yellow>[KẾT THÚC PIN TRIAL {currentTrialIndex}]</color> Thời gian hoàn thành: {totalTime:F2}s | FPS: {averageFps:F1}");

        AdvanceExperimentProgression();
        
        // Nghỉ một chút hoặc bắt đầu ngay trial mới
        StartNewTrial();
    }

    private void WriteTrialToCsv(int participant, int trial, string res, int pinsReq, float start, float end, float totalTime, float fps)
    {
        string participantStr = $"P{participant:D2}";
        string csvLine = $"{participantStr},{trial},{res},{pinsReq},{start:F2},{end:F2},{totalTime:F2},{fps:F1}\n";
        
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
}