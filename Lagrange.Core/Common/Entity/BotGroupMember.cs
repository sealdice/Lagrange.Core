namespace Lagrange.Core.Common.Entity;

public class BotGroupMember
{
    internal BotGroupMember(uint uin, string uid, GroupMemberPermission permission, uint groupLevel, string memberCard, 
        string memberName, DateTime joinTime, DateTime lastMsgTime)
    {
        Uin = uin;
        Uid = uid;
        Permission = permission;
        GroupLevel = groupLevel;
        MemberCard = memberCard;
        MemberName = memberName;
        JoinTime = joinTime;
        LastMsgTime = lastMsgTime;
    }
    
    public uint Uin { get; set; }
    
    internal string Uid { get; set; }

    public GroupMemberPermission Permission { get; set; }
    
    public uint GroupLevel { get; set; }
    
    public string MemberCard { get; set; }
    
    public string MemberName { get; set; }
    
    public DateTime JoinTime { get; set; }
    
    public DateTime LastMsgTime { get; set; }
}

public enum GroupMemberPermission : uint
{
    Member = 0,
    Owner = 1,
    Admin = 2,
}