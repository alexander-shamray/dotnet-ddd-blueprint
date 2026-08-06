using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

public class EntityTests
{
    [Fact]
    public void Two_entities_with_the_same_id_are_the_same_thing()
    {
        var id = TestId.New();

        var one = new TestEntity(id, "as it was");
        var other = new TestEntity(id, "as it is now");

        one.Equals(other).ShouldBeTrue();
    }

    [Fact]
    public void Two_entities_with_different_ids_are_different_things()
    {
        var one = new TestEntity(TestId.New(), "same label");
        var other = new TestEntity(TestId.New(), "same label");

        one.Equals(other).ShouldBeFalse();
    }

    [Fact]
    public void Two_entity_types_sharing_an_id_are_not_the_same_thing()
    {
        var id = TestId.New();

        var entity = new TestEntity(id, "label");
        var other = new OtherTestEntity(id);

        entity.Equals(other).ShouldBeFalse();
    }

    [Fact]
    public void Equal_entities_share_a_hash_code()
    {
        var id = TestId.New();

        var one = new TestEntity(id, "as it was");
        var other = new TestEntity(id, "as it is now");

        one.GetHashCode().ShouldBe(other.GetHashCode());
    }

    [Fact]
    public void The_equality_operator_agrees_with_Equals()
    {
        var id = TestId.New();

        var one = new TestEntity(id, "as it was");
        var other = new TestEntity(id, "as it is now");

        (one == other).ShouldBeTrue();
        (one != other).ShouldBeFalse();
    }

    [Fact]
    public void An_entity_is_never_equal_to_null()
    {
        var entity = new TestEntity(TestId.New(), "label");

        entity.Equals(null).ShouldBeFalse();
        (entity == null).ShouldBeFalse();
        (null == entity).ShouldBeFalse();
    }

    [Fact]
    public void Two_null_references_are_equal()
    {
        TestEntity? one = null;
        TestEntity? other = null;

        (one == other).ShouldBeTrue();
    }
}
