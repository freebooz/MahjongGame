import {readFileSync} from "node:fs";
import {globSync} from "node:fs";

// 管理台禁止把令牌写入 Web Storage；该轻量门禁与 TypeScript 编译共同组成 npm lint 入口。
const files=globSync("src/**/*.ts");
const forbidden=/\b(?:localStorage|sessionStorage)\b/;
const violations=files.filter(file=>forbidden.test(readFileSync(file,"utf8")));
if(violations.length){
  throw new Error(`管理台源码禁止持久化浏览器凭据：${violations.join(", ")}`);
}
