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
    [Tooltip("Prefab của vòng tròn đích (dùng để instantiate ngẫu nhiên). " +
             "Để trống nếu chỉ cần đếm pin không cần hiển thị target.")]
    public GameObject targetPrefab;
    [Tooltip("Vị trí gốc (tâm vùng spawn) để sinh ra các điểm cần ghim. " +
             "Để trống nếu chỉ cần đếm pin không cần hiển thị target.")]
    public Transform targetAnchor;
    [Tooltip("Bán kính tối đa (mét) để sinh ra điểm ghim quanh Anchor")]
    public float spawnRadius = 0.3f;
    [Tooltip("Khoảng cách tối đa (mét) để xác định ghim vào đúng target. " +
             "Khuyến nghị: 0.10 đến 0.20 cho VR tracking.")]
    public float pinTolerance = 0.15f;

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

    private float fpsAccumulator = 0f;
    private int fpsFrameCount = 0;

    void Start()
    {
        string folderPath = @"C:\Users\toikh\Documents\3D_Museum\aoDai_prj\VR_integration\UserStudy";
        
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            Debug.Log($"[ClothPinLogger] Đã tự động tạo thư mục: {folderPath}");
        }

        csvFilePath = Path.Combine(folderPath, "ClothPin_Experiment_RawData.csv");
        EnsureCsvHeaderAndResumeState();

        // ✅ FIX: Cảnh báo sớm nếu thiếu reference, thay vì âm thầm không ghi gì
        if (targetPrefab == null)
            Debug.LogWarning("⚠️ [ClothPinLogger] targetPrefab chưa được gán! " +
                             "Targets sẽ không hiển thị, nhưng mọi pin đều được đếm và ghi CSV bình thường.");
        if (targetAnchor == null)
            Debug.LogWarning("⚠️ [ClothPinLogger] targetAnchor chưa được gán! " +
                             "Targets sẽ không hiển thị, nhưng mọi pin đều được đếm và ghi CSV bình thường.");
        
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
            string header = "Participant,Trial,Resolution,Pins Required,Start Time,End Time,Total Time (s),FPS Avg\n";
            File.WriteAllText(csvFilePath, header);
            currentParticipantID = 1;
            currentTrialIndex = 1;
            Debug.Log($"[ClothPinLogger] Đã tạo file CSV mới tại: {csvFilePath}. Bắt đầu từ P01.");
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
        foreach(var t in currentTargets)
        {
            if(t != null) Destroy(t);
        }
        currentTargets.Clear();

        // Nếu thiếu reference thì bỏ qua phần spawn — đếm pin vẫn hoạt động bình thường
        if (targetPrefab == null || targetAnchor == null) return;

        for (int i = 0; i < count; i++)
        {
            Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * spawnRadius;
            randomOffset.z = 0;
            
            Vector3 spawnPos = targetAnchor.position + randomOffset;
            GameObject newTarget = Instantiate(targetPrefab, spawnPos, Quaternion.identity);
            newTarget.name = $"PinTarget_{i}";
            currentTargets.Add(newTarget);
        }
    }

    /// <summary>
    /// Được gọi từ UClothPinner khi người dùng ghim thành công một điểm.
    /// Mọi pin hợp lệ đều được đếm và ghi CSV.
    /// Nếu có targets đang hiển thị thì cũng kiểm tra độ gần để destroy target tương ứng.
    /// </summary>
    public void NotifyPinPlaced(Vector3 pinPosition)
    {
        if (!IsTrialActive)
        {
            Debug.LogWarning("[ClothPinLogger] NotifyPinPlaced được gọi nhưng IsTrialActive = false. Bỏ qua.");
            return;
        }

        // ✅ FIX: Kiểm tra target là tuỳ chọn — không chặn việc đếm pin
        if (currentTargets.Count > 0)
        {
            bool hitTarget = false;
            for (int i = currentTargets.Count - 1; i >= 0; i--)
            {
                if (currentTargets[i] != null)
                {
                    float dist = Vector3.Distance(pinPosition, currentTargets[i].transform.position);
                    // In log khoảng cách để dễ tinh chỉnh pinTolerance trong Inspector
                    Debug.Log($"[LOG] Khoảng cách tới PinTarget_{i}: {dist * 100f:F1}cm (tolerance = {pinTolerance * 100f:F0}cm)");

                    if (dist <= pinTolerance)
                    {
                        Destroy(currentTargets[i]);
                        currentTargets.RemoveAt(i);
                        hitTarget = true;
                        Debug.Log($"[LOG] ✅ Ghim trúng target! (Cách đích: {dist * 100f:F1}cm)");
                        break;
                    }
                }
            }

            if (!hitTarget)
            {
                // Cảnh báo nhưng vẫn đếm — giúp phát hiện nếu targetAnchor đặt sai chỗ
                Debug.LogWarning($"[LOG] ⚠️ Pin tại {pinPosition} không trúng target nào trong phạm vi {pinTolerance * 100f:F0}cm. " +
                                 "Vẫn đếm vào tổng. Nếu thấy cảnh báo này thường xuyên, hãy kiểm tra vị trí targetAnchor " +
                                 "hoặc tăng pinTolerance trong Inspector.");
            }
        }
        else
        {
            // Không có target nào (chưa gán prefab/anchor) — đây là mode đếm đơn giản
            Debug.Log("[LOG] Không có target nào trong scene (targetPrefab/targetAnchor chưa gán). Đếm pin trực tiếp.");
        }

        // ✅ FIX: Luôn đếm pin bất kể có trúng target hay không
        currentPinsPlaced++;
        Debug.Log($"[LOG] Tổng pin đã đặt: {currentPinsPlaced}/{currentPinsRequired}");

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
        
        StartNewTrial();
    }

    private void WriteTrialToCsv(int participant, int trial, string res, int pinsReq, float start, float end, float totalTime, float fps)
    {
        string participantStr = $"P{participant:D2}";
        string csvLine = $"{participantStr},{trial},{res},{pinsReq},{start:F2},{end:F2},{totalTime:F2},{fps:F1}\n";
        
        try
        {
            File.AppendAllText(csvFilePath, csvLine);
            Debug.Log($"[ClothPinLogger] ✅ Đã ghi dữ liệu vào CSV: {csvLine.Trim()}");
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