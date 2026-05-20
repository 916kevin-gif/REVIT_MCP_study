#!/usr/bin/env node

/**
 * Revit MCP Server
 * Bridge between AI and Revit via MCP
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
    CallToolRequestSchema,
    ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { RevitSocketClient } from "./socket.js";
import { registerRevitTools, executeRevitTool } from "./tools/revit-tools.js";

const server = new Server(
    {
        name: "revit-mcp-server",
        version: "1.0.0",
    },
    {
        capabilities: {
            tools: {},
        },
    }
);

const revitClient = new RevitSocketClient();

server.setRequestHandler(ListToolsRequestSchema, async () => {
    const tools = registerRevitTools();
    console.error(`[MCP Server] Registered ${tools.length} Revit tools`);
    return { tools };
});

server.setRequestHandler(CallToolRequestSchema, async (request) => {
    console.error(`[MCP Server] Calling tool: ${request.params.name}`);
    console.error(`[MCP Server] Arguments:`, JSON.stringify(request.params.arguments, null, 2));

    try {
        if (!revitClient.isConnected()) {
            console.error("[MCP Server] Revit not connected, attempting to connect...");
            await revitClient.connect();
        }

        const result = await executeRevitTool(
            request.params.name,
            request.params.arguments || {},
            revitClient
        );

        console.error("[MCP Server] Tool executed successfully");

        let text = JSON.stringify(result, null, 2);

        if (
            typeof result === "object" &&
            result !== null &&
            "RequiresUserInput" in result &&
            (result as { RequiresUserInput?: boolean }).RequiresUserInput === true
        ) {
            text = [
                "USER_SELECTION_REQUIRED",
                "請勿自動選擇或重試其他 seed。",
                "請先把結果顯示給使用者，等待使用者明確選擇後再繼續。",
                "",
                text,
            ].join("\n");
        }

        return {
            content: [
                {
                    type: "text",
                    text,
                },
            ],
        };
    } catch (error) {
        const errorMessage = error instanceof Error ? error.message : String(error);
        console.error(`[MCP Server] Tool execution failed: ${errorMessage}`);

        return {
            content: [
                {
                    type: "text",
                    text: `Error: ${errorMessage}`,
                },
            ],
            isError: true,
        };
    }
});

async function main() {
    console.error("Revit MCP Server starting...");
    console.error("Waiting for Revit Plugin...");

    const transport = new StdioServerTransport();
    await server.connect(transport);

    const configuredPort = process.env.REVIT_MCP_PORT || "8964";
    console.error("MCP Server Started");
    console.error(`Socket Server listening on ${configuredPort}`);

    const shutdown = async () => {
        console.error("\n[MCP Server] Shutting down...");
        try {
            await revitClient.disconnect();
            console.error("[MCP Server] Disconnected from Revit");
        } catch {
            // Ignore disconnect errors during shutdown.
        }
        process.exit(0);
    };

    process.on("SIGINT", shutdown);
    process.on("SIGTERM", shutdown);
}

main().catch((error) => {
    console.error("Server startup failed", error);
    process.exit(1);
});
