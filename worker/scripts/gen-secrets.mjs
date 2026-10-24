// Gera valores aleatórios para PANEL_API_KEY e TOKEN_ENC_KEY.
import { randomBytes } from "node:crypto";

console.log(`PANEL_API_KEY=${randomBytes(32).toString("base64url")}`);
console.log(`TOKEN_ENC_KEY=${randomBytes(32).toString("base64")}`);
