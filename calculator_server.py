#!/usr/bin/env python3
"""
Simple MCP Calculator Server

This server exposes mathematical operations as MCP tools.
"""
from mcp.server.fastmcp import FastMCP, Context
import logging
logging.basicConfig(level=logging.DEBUG)

# Create our MCP server
mcp = FastMCP("Calculator", debug=True)

@mcp.tool()
def add(a: float, b: float) -> float:
    """Add two numbers.
    
    Args:
        a: First number
        b: Second number
        
    Returns:
        The sum of a and b
    """
    return a + b

@mcp.tool()
def subtract(a: float, b: float) -> float:
    """Subtract b from a.
    
    Args:
        a: First number
        b: Second number
        
    Returns:
        The result of a - b
    """
    return a - b

@mcp.tool()
def multiply(a: float, b: float) -> float:
    """Multiply two numbers.
    
    Args:
        a: First number
        b: Second number
        
    Returns:
        The product of a and b
    """
    return a * b

@mcp.tool()
def divide(a: float, b: float) -> float:
    """Divide a by b.
    
    Args:
        a: Numerator
        b: Denominator (must not be zero)
        
    Returns:
        The result of a / b
        
    Raises:
        ValueError: If b is zero
    """
    if b == 0:
        raise ValueError("Cannot divide by zero")
    return a / b

@mcp.tool()
def power(base: float, exponent: float) -> float:
    """Calculate base raised to the power of exponent.
    
    Args:
        base: The base number
        exponent: The exponent
        
    Returns:
        base ^ exponent
    """
    return base ** exponent

@mcp.tool()
def calculate(expression: str) -> float:
    """Evaluate a mathematical expression.
    
    Args:
        expression: A mathematical expression string
        
    Returns:
        The result of evaluating the expression
        
    Note:
        Uses Python's eval function with limited scope for basic calculations
    """
    # For security, only allow basic math operations with a limited scope
    allowed_names = {"abs": abs, "max": max, "min": min, "pow": pow, "round": round}
    
    try:
        # Evaluate the expression with limited scope
        result = eval(expression, {"__builtins__": {}}, allowed_names)
        return float(result)
    except Exception as e:
        raise ValueError(f"Invalid expression: {str(e)}")

@mcp.resource("calculator://help")
def get_calculator_help() -> str:
    """Provide help information about the calculator.
    
    Returns:
        Help text describing the calculator functions
    """
    return """
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
    """

@mcp.prompt()
def calculation_prompt(expression: str) -> str:
    """Create a prompt for calculating a mathematical expression.
    
    Args:
        expression: The mathematical expression to evaluate
        
    Returns:
        A prompt for calculating the expression
    """
    return f"""Please evaluate this mathematical expression: {expression}
    
You can use the calculator tools if needed.
"""

if __name__ == "__main__":
    # Run the server
    mcp.run()