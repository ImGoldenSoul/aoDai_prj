using UnityEngine;
using UnityEngine.InputSystem; // BẮT BUỘC THÊM DÒNG NÀY
using UCloth;
using Unity.Mathematics;
using System.Collections.Generic;
using System.IO;
using System;
using System.Reflection;
using Unity.Collections;

[RequireComponent(typeof(UCCloth))]
public class UClothVelocityLogger : MonoBehaviour
{
    [Header("Logger Settings")]
    [Tooltip("Đường dẫn lưu file trên máy tính")]
    public string saveDirectory = @"D:\Đồ Án 2\csv";

    [Tooltip("Tên file (Nhớ đổi tên khi test bản lỗi và bản xịn)")]
    public string fileName = "SoftPin_Test.csv";

    public bool isLogging = false;

    private UCCloth clothComponent;
    private NativeArray<float3> clothVelocities;
    private bool arraysExtracted = false;

    // Danh sách lưu dữ liệu: <Thời gian, Vận tốc tối đa>
    private List<string> logData = new List<string>();
    private float startTime;

    void Start()
    {
        clothComponent = GetComponent<UCCloth>();
        clothComponent.OnSimulationFinished += OnSimulationFinishedSafe;
    }

    void Update()
    {
        if (!arraysExtracted && clothComponent.simData != null) ExtractInternalData();

        // Sử dụng New Input System để đọc phím Enter
        bool enterPressed = Keyboard.current != null && Keyboard.current.enterKey.wasPressedThisFrame;

        // Bấm phím Enter để bắt đầu/kết thúc ghi số liệu
        if (enterPressed)
        {
            isLogging = !isLogging;
            if (isLogging)
            {
                startTime = Time.time;
                logData.Clear();
                logData.Add("Time(s),PeakVelocity(m/s)"); // Header của file CSV
                Debug.Log($"[Logger] ▶️ Đã BẮT ĐẦU ghi dữ liệu...");
            }
            else
            {
                SaveToCSV();
            }
        }
    }

    private void OnSimulationFinishedSafe(object sender, EventArgs e)
    {
        if (isLogging && arraysExtracted && clothVelocities.IsCreated)
        {
            float maxVelocity = 0f;
            // Quét qua toàn bộ lưới vải tìm hạt bị giật mạnh nhất
            for (int i = 0; i < clothVelocities.Length; i++)
            {
                float speed = math.length(clothVelocities[i]);
                if (speed > maxVelocity) maxVelocity = speed;
            }

            float currentTime = Time.time - startTime;
            logData.Add($"{currentTime:F3},{maxVelocity:F3}");
        }
    }

    private void ExtractInternalData()
    {
        Type simDataType = clothComponent.simData.GetType();
        FieldInfo velField = simDataType.GetField("cVelocity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (velField != null) clothVelocities = (NativeArray<float3>)velField.GetValue(clothComponent.simData);
        if (clothVelocities.IsCreated) arraysExtracted = true;
    }

    private void SaveToCSV()
    {
        try
        {
            // Kiểm tra xem ổ D đã có thư mục này chưa, chưa có thì tự động tạo mới
            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            // Gộp đường dẫn và tên file lại
            string fullPath = Path.Combine(saveDirectory, fileName);

            // Ghi file
            File.WriteAllLines(fullPath, logData);
            Debug.Log($"[Logger] ✅ Đã LƯU thành công file CSV tại: {fullPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Logger] ❌ Có lỗi xảy ra khi lưu file: {ex.Message}");
        }
    }

    void OnDestroy()
    {
        if (clothComponent != null) clothComponent.OnSimulationFinished -= OnSimulationFinishedSafe;
    }
}