#!/usr/bin/env node
/**
 * Simple MCP Calculator Server
 * 
 * This server exposes mathematical operations as MCP tools.
 */
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

// Create server instance
const server = new McpServer({
  name: "Calculator",
  version: "1.0.0",
});

// Add tool
server.tool(
  "add",
  "Add two numbers",
  {
    a: z.number().describe("First number"),
    b: z.number().describe("Second number"),
  },
  async ({ a, b }) => {
    return {
      content: [
        {
          type: "text",
          text: "42",//String(a + b),
        },
      ],
    };
  }
);

// Subtract tool
server.tool(
  "subtract",
  "Subtract b from a",
  {
    a: z.number().describe("First number"),
    b: z.number().describe("Second number"),
  },
  async ({ a, b }) => {
    return {
      content: [
        {
          type: "text",
          text: String(a - b),
        },
      ],
    };
  }
);

// Multiply tool
server.tool(
  "multiply",
  "Multiply two numbers",
  {
    a: z.number().describe("First number"),
    b: z.number().describe("Second number"),
  },
  async ({ a, b }) => {
    return {
      content: [
        {
          type: "text",
          text: String(a * b),
        },
      ],
    };
  }
);

// Divide tool
server.tool(
  "divide",
  "Divide a by b",
  {
    a: z.number().describe("Numerator"),
    b: z.number().describe("Denominator (must not be zero)"),
  },
  async ({ a, b }) => {
    if (b === 0) {
      return {
        isError: true,
        content: [
          {
            type: "text",
            text: "Error: Cannot divide by zero",
          },
        ],
      };
    }
    
    return {
      content: [
        {
          type: "text",
          text: String(a / b),
        },
      ],
    };
  }
);

// Power tool
server.tool(
  "power",
  "Calculate base raised to the power of exponent",
  {
    base: z.number().describe("The base number"),
    exponent: z.number().describe("The exponent"),
  },
  async ({ base, exponent }) => {
    return {
      content: [
        {
          type: "text",
          text: String(Math.pow(base, exponent)),
        },
      ],
    };
  }
);

// Calculate expression tool
server.tool(
  "calculate",
  "Evaluate a mathematical expression",
  {
    expression: z.string().describe("A mathematical expression string"),
  },
  async ({ expression }) => {
    try {
      // Note: In a production environment, you would want to use
      // a safer expression evaluator than eval
      const result = eval(expression);
      
      return {
        content: [
          {
            type: "text",
            text: String(result),
          },
        ],
      };
    } catch (error) {
      return {
        isError: true,
        content: [
          {
            type: "text",
            text: `Error: Invalid expression - ${error.message}`,
          },
        ],
      };
    }
  }
);

// Add a resource
server.resource({
  uri: "calculator://help",
  name: "Calculator Help",
  description: "Help information about the calculator",
  async read() {
    return {
      contents: [
        {
          uri: "calculator://help",
          mimeType: "text/plain",
          text: `
Calculator MCP Server

This server provides basic mathematical operations:

- add(a, b) - Add two numbers
- subtract(a, b) - Subtract b from a
- multiply(a, b) - Multiply two numbers
- divide(a, b) - Divide a by b (b cannot be zero)
- power(base, exponent) - Calculate base raised to the power of exponent
- calculate(expression) - Evaluate a mathematical expression

Examples:
  add(5, 3) -> 8
  subtract(10, 4) -> 6
  multiply(2.5, 4) -> 10
  divide(10, 2) -> 5
  power(2, 3) -> 8
  calculate("10 + 5 * 2") -> 20
          `,
        },
      ],
    };
  },
});

// Add a prompt
server.prompt({
  name: "calculation",
  description: "Create a prompt for calculating a mathematical expression",
  arguments: [
    {
      name: "expression",
      description: "The mathematical expression to evaluate",
      required: true,
    },
  ],
  async get(args) {
    const expression = args.arguments?.expression || "";
    
    return {
      messages: [
        {
          role: "user",
          content: {
            type: "text",
            text: `Please evaluate this mathematical expression: ${expression}\n\nYou can use the calculator tools if needed.`,
          },
        },
      ],
    };
  },
});

// Start server
async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Calculator MCP Server running on stdio");
}

main().catch((error) => {
  console.error("Fatal error:", error);
  process.exit(1);
});