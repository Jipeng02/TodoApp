// See https://aka.ms/new-console-template for more information

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Hello, TodoApp!");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(); // 等待用户按键
    }
}