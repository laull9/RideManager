using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RideManager.Core;
using RideManager.Utils;

namespace RideManager.Data;

/// <summary>
/// 表示 RideManager 的 PostgreSQL EF Core 上下文。
/// </summary>
public sealed class RideManagerDbContext : DbContext
{
    /// <summary>
    /// 创建数据库上下文。
    /// </summary>
    public RideManagerDbContext(DbContextOptions<RideManagerDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();

    public DbSet<ModelArtifactEntity> ModelArtifacts => Set<ModelArtifactEntity>();

    public DbSet<RunSessionEntity> RunSessions => Set<RunSessionEntity>();

    public DbSet<SafetyDecisionEntity> SafetyDecisions => Set<SafetyDecisionEntity>();

    public DbSet<CameraFrameEntity> CameraFrames => Set<CameraFrameEntity>();

    public DbSet<CameraFindingEntity> CameraFindings => Set<CameraFindingEntity>();

    public DbSet<SensorSnapshotEntity> SensorSnapshots => Set<SensorSnapshotEntity>();

    public DbSet<SensorReadingEntity> SensorReadings => Set<SensorReadingEntity>();

    public DbSet<ActuatorCommandEntity> ActuatorCommands => Set<ActuatorCommandEntity>();

    public DbSet<SystemEventEntity> SystemEvents => Set<SystemEventEntity>();

    /// <summary>
    /// 根据数据库配置创建 PostgreSQL 上下文。
    /// </summary>
    public static RideManagerDbContext Create(DatabaseOptions options)
    {
        var builder = new DbContextOptionsBuilder<RideManagerDbContext>();
        builder.UseNpgsql(options.ConnectionString);
        return new RideManagerDbContext(builder.Options);
    }

    /// <summary>
    /// 配置表结构、索引和关系。
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDevices(modelBuilder);
        ConfigureModelArtifacts(modelBuilder);
        ConfigureRunSessions(modelBuilder);
        ConfigureSafetyDecisions(modelBuilder);
        ConfigureCameraFrames(modelBuilder);
        ConfigureCameraFindings(modelBuilder);
        ConfigureSensorSnapshots(modelBuilder);
        ConfigureSensorReadings(modelBuilder);
        ConfigureActuatorCommands(modelBuilder);
        ConfigureSystemEvents(modelBuilder);
        UseSnakeCaseNames(modelBuilder);
    }

    /// <summary>
    /// 配置设备注册表。
    /// </summary>
    private static void ConfigureDevices(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DeviceEntity>();
        entity.ToTable("devices");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => value.Code).IsUnique();
        entity.Property(value => value.Code).HasMaxLength(64).IsRequired();
        entity.Property(value => value.DeviceType).HasMaxLength(32).IsRequired();
        entity.Property(value => value.Transport).HasMaxLength(32);
        entity.Property(value => value.Address).HasMaxLength(256);
        entity.Property(value => value.ConfigJson).HasColumnType("jsonb");
    }

    /// <summary>
    /// 配置模型资产表。
    /// </summary>
    private static void ConfigureModelArtifacts(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ModelArtifactEntity>();
        entity.ToTable("model_artifacts");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.Name, value.Backend, value.Version });
        entity.Property(value => value.Name).HasMaxLength(128).IsRequired();
        entity.Property(value => value.Backend).HasMaxLength(16).IsRequired();
        entity.Property(value => value.RelativePath).HasMaxLength(512).IsRequired();
        entity.Property(value => value.Version).HasMaxLength(64);
        entity.Property(value => value.LabelsJson).HasColumnType("jsonb");
        entity.Property(value => value.ConfigJson).HasColumnType("jsonb");
    }

    /// <summary>
    /// 配置运行会话表。
    /// </summary>
    private static void ConfigureRunSessions(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RunSessionEntity>();
        entity.ToTable("run_sessions");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => value.StartedAt);
        entity.Property(value => value.HostName).HasMaxLength(128);
        entity.Property(value => value.ConfigJson).HasColumnType("jsonb");
        entity.Property(value => value.Note).HasMaxLength(512);
    }

    /// <summary>
    /// 配置安全决策表。
    /// </summary>
    private static void ConfigureSafetyDecisions(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SafetyDecisionEntity>();
        entity.ToTable("safety_decisions");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => value.DecidedAt);
        entity.HasIndex(value => value.RiskLevel);
        entity.Property(value => value.RiskLevel).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
        entity
            .HasOne(value => value.RunSession)
            .WithMany()
            .HasForeignKey(value => value.RunSessionId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>
    /// 配置摄像头帧处理表。
    /// </summary>
    private static void ConfigureCameraFrames(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CameraFrameEntity>();
        entity.ToTable("camera_frames");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.CameraId, value.CapturedAt });
        entity.Property(value => value.CameraId).HasMaxLength(32).IsRequired();
        entity.Property(value => value.MetadataJson).HasColumnType("jsonb");
        entity
            .HasOne(value => value.SafetyDecision)
            .WithMany(value => value.CameraFrames)
            .HasForeignKey(value => value.SafetyDecisionId)
            .OnDelete(DeleteBehavior.Cascade);
        entity
            .HasOne(value => value.Device)
            .WithMany()
            .HasForeignKey(value => value.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>
    /// 配置摄像头检测结果表。
    /// </summary>
    private static void ConfigureCameraFindings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CameraFindingEntity>();
        entity.ToTable("camera_findings");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.CameraId, value.ObservedAt });
        entity.HasIndex(value => value.Label);
        entity.Property(value => value.CameraId).HasMaxLength(32).IsRequired();
        entity.Property(value => value.Label).HasMaxLength(128).IsRequired();
        entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
        entity
            .HasOne(value => value.SafetyDecision)
            .WithMany(value => value.CameraFindings)
            .HasForeignKey(value => value.SafetyDecisionId)
            .OnDelete(DeleteBehavior.Cascade);
        entity
            .HasOne(value => value.CameraFrame)
            .WithMany(value => value.Findings)
            .HasForeignKey(value => value.CameraFrameId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>
    /// 配置传感器快照表。
    /// </summary>
    private static void ConfigureSensorSnapshots(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SensorSnapshotEntity>();
        entity.ToTable("sensor_snapshots");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.SensorName, value.ObservedAt });
        entity.Property(value => value.SensorName).HasMaxLength(64).IsRequired();
        entity.Property(value => value.ValuesJson).HasColumnType("jsonb");
        entity
            .HasOne(value => value.SafetyDecision)
            .WithMany(value => value.SensorSnapshots)
            .HasForeignKey(value => value.SafetyDecisionId)
            .OnDelete(DeleteBehavior.Cascade);
        entity
            .HasOne(value => value.Device)
            .WithMany()
            .HasForeignKey(value => value.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>
    /// 配置传感器指标明细表。
    /// </summary>
    private static void ConfigureSensorReadings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SensorReadingEntity>();
        entity.ToTable("sensor_readings");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => value.Metric);
        entity.Property(value => value.Metric).HasMaxLength(64).IsRequired();
        entity.Property(value => value.Unit).HasMaxLength(32);
        entity
            .HasOne(value => value.SensorSnapshot)
            .WithMany(value => value.Readings)
            .HasForeignKey(value => value.SensorSnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// 配置执行器命令表。
    /// </summary>
    private static void ConfigureActuatorCommands(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ActuatorCommandEntity>();
        entity.ToTable("actuator_commands");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.ActuatorName, value.RequestedAt });
        entity.HasIndex(value => value.Status);
        entity.Property(value => value.ActuatorName).HasMaxLength(64).IsRequired();
        entity.Property(value => value.CommandType).HasMaxLength(64).IsRequired();
        entity.Property(value => value.Status).HasMaxLength(32).IsRequired();
        entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
        entity.Property(value => value.ErrorMessage).HasMaxLength(1024);
        entity
            .HasOne(value => value.SafetyDecision)
            .WithMany(value => value.ActuatorCommands)
            .HasForeignKey(value => value.SafetyDecisionId)
            .OnDelete(DeleteBehavior.SetNull);
        entity
            .HasOne(value => value.Device)
            .WithMany()
            .HasForeignKey(value => value.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>
    /// 配置系统事件表。
    /// </summary>
    private static void ConfigureSystemEvents(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SystemEventEntity>();
        entity.ToTable("system_events");
        entity.HasKey(value => value.Id);
        entity.HasIndex(value => new { value.Source, value.OccurredAt });
        entity.HasIndex(value => value.Level);
        entity.Property(value => value.Source).HasMaxLength(64).IsRequired();
        entity.Property(value => value.Level).HasMaxLength(16).IsRequired();
        entity.Property(value => value.Message).HasMaxLength(1024).IsRequired();
        entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
    }

    /// <summary>
    /// 将 EF 默认标识统一改为 PostgreSQL 常用的 snake_case。
    /// </summary>
    private static void UseSnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName() ?? string.Empty));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName() ?? string.Empty));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName() ?? string.Empty));
            }
        }
    }

    /// <summary>
    /// 将 PascalCase 名称转换为 snake_case。
    /// </summary>
    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}

/// <summary>
/// 为 dotnet ef migrations 提供设计时上下文。
/// </summary>
public sealed class RideManagerDbContextFactory : IDesignTimeDbContextFactory<RideManagerDbContext>
{
    /// <summary>
    /// 根据 config.toml 创建设计时上下文。
    /// </summary>
    public RideManagerDbContext CreateDbContext(string[] args)
    {
        var configPath = args.Length > 0 ? args[0] : "config.toml";
        var options = ConfigLoader.Load(configPath).Database;
        return RideManagerDbContext.Create(options);
    }
}
