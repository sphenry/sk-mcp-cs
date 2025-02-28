#!/usr/bin/env node
/**
 * Simple MCP Weather Server
 * 
 * This server provides weather information tools through MCP.
 */
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

// Create server instance
const server = new McpServer({
  name: "WeatherServer",
  version: "1.0.0",
});

// Mock weather data (for demonstration without API keys)
const cities = {
  "new york": {
    temperature: 72,
    conditions: "Partly Cloudy",
    humidity: 65,
    windSpeed: 8,
    forecast: [
      { day: "Today", high: 72, low: 58, conditions: "Partly Cloudy" },
      { day: "Tomorrow", high: 75, low: 60, conditions: "Sunny" },
      { day: "Wednesday", high: 68, low: 55, conditions: "Rainy" }
    ]
  },
  "london": {
    temperature: 62,
    conditions: "Rainy",
    humidity: 80,
    windSpeed: 12,
    forecast: [
      { day: "Today", high: 62, low: 52, conditions: "Rainy" },
      { day: "Tomorrow", high: 65, low: 54, conditions: "Overcast" },
      { day: "Wednesday", high: 67, low: 55, conditions: "Partly Cloudy" }
    ]
  },
  "tokyo": {
    temperature: 78,
    conditions: "Sunny",
    humidity: 60,
    windSpeed: 5,
    forecast: [
      { day: "Today", high: 78, low: 65, conditions: "Sunny" },
      { day: "Tomorrow", high: 80, low: 67, conditions: "Sunny" },
      { day: "Wednesday", high: 79, low: 68, conditions: "Partly Cloudy" }
    ]
  },
  "sydney": {
    temperature: 65,
    conditions: "Overcast",
    humidity: 70,
    windSpeed: 15,
    forecast: [
      { day: "Today", high: 65, low: 55, conditions: "Overcast" },
      { day: "Tomorrow", high: 68, low: 57, conditions: "Partly Cloudy" },
      { day: "Wednesday", high: 70, low: 58, conditions: "Sunny" }
    ]
  },
  "paris": {
    temperature: 70,
    conditions: "Clear",
    humidity: 55,
    windSpeed: 7,
    forecast: [
      { day: "Today", high: 70, low: 58, conditions: "Clear" },
      { day: "Tomorrow", high: 72, low: 60, conditions: "Sunny" },
      { day: "Wednesday", high: 68, low: 57, conditions: "Partly Cloudy" }
    ]
  }
};

// Default city if not found
const defaultCity = {
  temperature: 70,
  conditions: "Unknown",
  humidity: 60,
  windSpeed: 10,
  forecast: [
    { day: "Today", high: 70, low: 60, conditions: "Unknown" },
    { day: "Tomorrow", high: 72, low: 62, conditions: "Unknown" },
    { day: "Wednesday", high: 71, low: 61, conditions: "Unknown" }
  ]
};

// Get current weather tool
server.tool(
  "get_current_weather",
  "Get the current weather for a city",
  {
    city: z.string().describe("The name of the city to get weather for"),
  },
  async ({ city }) => {
    console.error(`Getting weather for: ${city}`);
    
    // Normalize city name for lookup
    const normalizedCity = city.toLowerCase().trim();
    const cityData = cities[normalizedCity] || defaultCity;
    
    const weatherReport = `Current weather for ${city}:
Temperature: ${cityData.temperature}°F
Conditions: ${cityData.conditions}
Humidity: ${cityData.humidity}%
Wind: ${cityData.windSpeed} mph`;

    return {
      content: [
        {
          type: "text",
          text: weatherReport,
        },
      ],
    };
  }
);

// Get forecast tool
server.tool(
  "get_forecast",
  "Get the weather forecast for a city",
  {
    city: z.string().describe("The name of the city to get the forecast for"),
    days: z.number().min(1).max(3).default(3).describe("Number of days in the forecast (1-3)"),
  },
  async ({ city, days }) => {
    console.error(`Getting ${days}-day forecast for: ${city}`);
    
    // Normalize city name for lookup
    const normalizedCity = city.toLowerCase().trim();
    const cityData = cities[normalizedCity] || defaultCity;
    
    // Limit forecast to requested days
    const forecastDays = cityData.forecast.slice(0, days);
    
    let forecastReport = `${days}-Day Forecast for ${city}:\n\n`;
    
    forecastDays.forEach(day => {
      forecastReport += `${day.day}: High ${day.high}°F, Low ${day.low}°F, ${day.conditions}\n`;
    });

    return {
      content: [
        {
          type: "text",
          text: forecastReport,
        },
      ],
    };
  }
);

// Check if city exists tool
server.tool(
  "is_city_supported",
  "Check if a city is supported by the weather service",
  {
    city: z.string().describe("The name of the city to check"),
  },
  async ({ city }) => {
    const normalizedCity = city.toLowerCase().trim();
    const supported = normalizedCity in cities;

    return {
      content: [
        {
          type: "text",
          text: supported 
            ? `Yes, ${city} is supported by our weather service.` 
            : `No, ${city} is not in our database, but we'll return default data.`,
        },
      ],
    };
  }
);

// List supported cities tool
server.tool(
  "list_supported_cities",
  "List all cities supported by the weather service",
  {},
  async () => {
    const cityList = Object.keys(cities).map(city => 
      city.split(' ').map(word => word.charAt(0).toUpperCase() + word.slice(1)).join(' ')
    );

    return {
      content: [
        {
          type: "text",
          text: `Supported cities: ${cityList.join(', ')}`,
        },
      ],
    };
  }
);

// Add a weather resource
server.resource({
  uri: "weather://help",
  name: "Weather Help",
  description: "Help information about the weather service",
  async read() {
    return {
      contents: [
        {
          uri: "weather://help",
          mimeType: "text/plain",
          text: `
Weather MCP Server

This server provides weather information for several cities:
- New York
- London
- Tokyo
- Sydney
- Paris

Available tools:
- get_current_weather(city): Get the current weather for a city
- get_forecast(city, days): Get a forecast for 1-3 days
- is_city_supported(city): Check if a city is supported
- list_supported_cities(): Get a list of all supported cities

Note: This server uses mock data for demonstration purposes.
          `,
        },
      ],
    };
  },
});

// Add a prompt template
server.prompt({
  name: "weather_query",
  description: "Create a prompt for asking about the weather",
  arguments: [
    {
      name: "city",
      description: "The city to check the weather for",
      required: true,
    },
  ],
  async get(args) {
    const city = args.arguments?.city || "New York";
    
    return {
      messages: [
        {
          role: "user",
          content: {
            type: "text",
            text: `What's the weather like in ${city} today and for the next few days? Please provide temperature, conditions, and any other relevant information.`,
          },
        },
      ],
    };
  },
});

// Start server
async function main() {
  console.error("Weather MCP Server starting...");
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Weather MCP Server running on stdio");
}

main().catch((error) => {
  console.error("Fatal error:", error);
  process.exit(1);
});