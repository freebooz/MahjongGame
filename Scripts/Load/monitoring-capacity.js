import http from "k6/http";
import { check } from "k6";

const baseUrl = __ENV.ADMIN_BASE_URL || "http://127.0.0.1:18083";
const token = __ENV.ADMIN_ACCESS_TOKEN;
const targetRequestRate = Number(
  __ENV.MONITORING_REQUESTS_PER_SECOND || 10);

export const options = {
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
  scenarios: {
    paginated_monitoring: {
      executor: "constant-arrival-rate",
      // 每次迭代固定读取三类首屏，并最多读取一个下一页；向下取整可确保真实请求率不超过配置值。
      rate: Math.max(1, Math.floor(targetRequestRate / 4)),
      timeUnit: "1s",
      duration: __ENV.MONITORING_DURATION || "5m",
      preAllocatedVUs: 50,
      maxVUs: 300,
      exec: "readPages"
    }
  },
  thresholds: {
    http_req_duration: ["p(95)<750", "p(99)<1500"],
    http_req_failed: ["rate<0.01"],
    checks: ["rate>0.99"]
  }
};

const headers = {
  Authorization: `Bearer ${token}`,
  Accept: "application/json"
};

// 每次迭代读取玩家、房间和实例首屏，并随机继续一类游标页，覆盖解析和索引路径。
export function readPages() {
  const responses = http.batch([
    ["GET", `${baseUrl}/admin/v1/players?pageSize=200`, null, { headers }],
    ["GET", `${baseUrl}/admin/v1/rooms?pageSize=200`, null, { headers }],
    ["GET", `${baseUrl}/admin/v1/instances?pageSize=200`, null, { headers }]
  ]);
  for (const [index, response] of responses.entries()) {
    check(response, {
      "分页状态为 200": item => item.status === 200,
      "页大小不超过 200": item => {
        try {
          return JSON.parse(item.body).items.length <= 200;
        } catch {
          return false;
        }
      }
    });

    // 玩家和房间种子数据必须能被真实读取，防止上游降级为空页后压测产生假阳性。
    if (index < 2) {
      check(response, {
        "目标数据集首屏非空": item => {
          try {
            return JSON.parse(item.body).items.length > 0;
          } catch {
            return false;
          }
        }
      });
    }
  }

  const page = responses[Math.floor(Math.random() * responses.length)];
  if (page.status !== 200) return;
  const body = page.json();
  if (!body.nextCursor) return;
  const separator = page.url.includes("?") ? "&" : "?";
  const next = http.get(
    `${page.url}${separator}cursor=${encodeURIComponent(body.nextCursor)}`,
    { headers });
  check(next, {
    "游标下一页状态为 200": item => item.status === 200
  });
}
