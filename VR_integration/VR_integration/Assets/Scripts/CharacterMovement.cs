using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI; // Bắt buộc phải có thư viện này để dùng NavMesh

public class CharacterMovement : MonoBehaviour
{
    private NavMeshAgent agent;

    [Header("Movement Settings")]
    public float moveSpeed = 10f;

    [Header("Input Setting")]
    [SerializeField] float sampleDistance = 0.5f; // Khoảng cách tìm điểm hợp lệ trên NavMesh
    [SerializeField] LayerMask groundLayer;       // Layer của sàn nhà để Raycast nhận diện

    // Giữ lại event này nếu bạn muốn làm hiệu ứng click chuột (nếu không dùng có thể xóa)
    public static event System.Action<Vector3> OnGroundTouch;

    void Start()
    {
        // Chỉ lấy NavMeshAgent, bỏ qua Animator
        agent = GetComponent<NavMeshAgent>();

        if (agent != null)
        {
            agent.speed = moveSpeed;
        }
        else
        {
            Debug.LogError("Chưa gắn component NavMeshAgent vào nhân vật!");
        }
    }

    void Update()
    {
        // Kiểm tra click chuột trái
        if (Input.GetMouseButtonDown(0))
        {
            // Tạo tia Ray từ camera đến vị trí chuột trên màn hình
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            // Bắn Ray chạm vào lớp GroundLayer
            if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, groundLayer))
            {
                // Kiểm tra xem điểm click có nằm trên (hoặc gần) NavMesh đã bake không
                if (NavMesh.SamplePosition(hit.point, out NavMeshHit navMeshHit, sampleDistance, NavMesh.AllAreas))
                {
                    // Ra lệnh cho agent di chuyển đến đó
                    agent.SetDestination(navMeshHit.position);

                    // Kích hoạt event (nếu có script khác lắng nghe để tạo hiệu ứng)
                    OnGroundTouch?.Invoke(navMeshHit.position);
                }
                else
                {
                    Debug.Log("Điểm click không nằm trong vùng NavMesh đi được.");
                }
            }
        }
    }
}