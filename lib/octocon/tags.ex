defmodule Octocon.Tags do
  @moduledoc false

  import Ecto.Query, warn: false

  alias Octocon.{
    Accounts,
    Alters,
    Friendships,
    Repo
  }

  alias Octocon.Tags.{
    AlterTag,
    Tag
  }

  alias OctoconWeb.System.TagJSON, as: TagRenderer

  def unwrap_system_identity_where(system_identity, extra \\ []) do
    case system_identity do
      {:system, system_id} ->
        [user_id: system_id] |> Keyword.merge(extra)

      {:discord, _} = identity ->
        [user_id: Accounts.id_from_system_identity(identity, :system)]
        |> Keyword.merge(extra)
    end
  end

  def get_tag(system_identity, tag_id) do
    where = unwrap_system_identity_where(system_identity, id: tag_id)

    query =
      Tag
      |> where(^where)
      |> join(:left, [t], at in AlterTag, on: t.id == at.tag_id and at.user_id == t.user_id)
      |> group_by([t], t.id)
      |> select([t, at], %{tag: t})
      |> select_merge([j, at], %{alters: fragment("array_agg(?)", at.alter_id)})

    Repo.one(query)
    |> case do
      # Populate virtual `alters` field
      nil -> nil
      %{tag: tag, alters: [nil]} -> %{tag | alters: []}
      %{tag: tag, alters: alters} -> %{tag | alters: alters}
    end
  end

  # Also returns guarded list of containing alters
  def get_tag_guarded(system_identity, tag_id, caller_identity) do
    where = unwrap_system_identity_where(system_identity, id: tag_id)

    query =
      Tag
      |> where(^where)
      |> join(:left, [t], at in AlterTag, on: t.id == at.tag_id and at.user_id == t.user_id)
      |> group_by([t], t.id)
      |> select([t, at], %{tag: t})
      |> select_merge([j, at], %{alters: fragment("array_agg(?)", at.alter_id)})

    tag =
      Repo.one(query)
      |> case do
        # Populate virtual `alters` field
        nil -> nil
        %{tag: tag, alters: [nil]} -> %{tag | alters: []}
        %{tag: tag, alters: alters} -> %{tag | alters: alters}
      end

    friendship_level = Friendships.get_friendship_level(system_identity, caller_identity)

    cond do
      tag == nil ->
        nil

      Alters.can_view_entity?(friendship_level, tag.security_level) ->
        process_guarded_tag(system_identity, tag, friendship_level)

      true ->
        nil
    end
  end

  defp process_guarded_tag(_system_identity, tag, _friendship_level)
       when is_map(tag) and tag.alters == [] do
    tag
  end

  defp process_guarded_tag(system_identity, tag, friendship_level) do
    alters = Alters.get_alters_guarded_bare_batch(system_identity, friendship_level, tag.alters)
    %{tag | alters: alters}
  end

  def get_tags(system_identity, opts \\ []) do
    where = unwrap_system_identity_where(system_identity)

    order_by = Keyword.get(opts, :order_by, desc: :inserted_at)

    query =
      Tag
      |> where(^where)
      |> join(:left, [t], at in AlterTag, on: t.id == at.tag_id and at.user_id == t.user_id)
      |> group_by([t], t.id)
      |> order_by(^order_by)
      |> select([t], %{tag: t})
      |> select_merge([t, at], %{alters: fragment("array_agg(?)", at.alter_id)})

    Repo.all(query)
    |> Enum.map(fn
      # Populate virtual `alters` field
      %{tag: tag, alters: [nil]} -> %{tag | alters: []}
      %{tag: tag, alters: alters} -> %{tag | alters: alters}
    end)
  end

  # OLD IMPLEMENTATION
  # def get_tags_guarded(system_identity, caller_identity) do
  #   where = unwrap_system_identity_where(system_identity)

  #   query =
  #     Tag
  #     |> where(^where)
  #     |> select([t], t)

  #   friendship_level = Friendships.get_friendship_level(system_identity, caller_identity)

  #   query
  #   |> Repo.all()
  #   |> Stream.filter(fn tag ->
  #     Alters.can_view_entity?(friendship_level, tag.security_level)
  #   end)
  #   |> Enum.map(fn tag -> %{tag | alters: []} end)
  # end

  def get_tags_guarded(system_identity, caller_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      Tag
      |> where(^where)
      |> select([t], t)

    friendship_level = Friendships.get_friendship_level(system_identity, caller_identity)

    Repo.all(query)
    |> Enum.filter(fn tag -> Alters.can_view_entity?(friendship_level, tag.security_level) end)
    |> Enum.map(fn tag -> %{tag | alters: []} end)
  end

  def create_tag(system_identity, name) do
    case Accounts.id_from_system_identity(system_identity, :system) do
      nil ->
        {:error, :not_found}

      system_id ->
        id = Ecto.UUID.generate()

        result =
          %Tag{
            id: id,
            user_id: system_id
          }
          |> Tag.changeset(%{name: name})
          |> Repo.insert()

        case result do
          {:ok, tag} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "tag_created",
                %{
                  tag: TagRenderer.data_me(%{tag | alters: []})
                }
              )
            end)

            {:ok, tag}

          _ ->
            {:error, :changeset}
        end
    end
  end

  def create_tag(system_identity, name, parent_tag_id) do
    case Accounts.id_from_system_identity(system_identity, :system) do
      nil ->
        {:error, :not_found}

      system_id ->
        case get_tag(system_identity, parent_tag_id) do
          nil ->
            {:error, :not_found}

          parent_tag when parent_tag.user_id != system_id ->
            {:error, :not_found}

          _ ->
            id = Ecto.UUID.generate()

            result =
              %Tag{
                id: id,
                user_id: system_id
              }
              |> Tag.changeset(%{name: name, parent_tag_id: parent_tag_id})
              |> Repo.insert()

            case result do
              {:ok, tag} ->
                spawn(fn ->
                  OctoconWeb.Endpoint.broadcast!(
                    "system:#{system_id}",
                    "tag_created",
                    %{
                      tag: TagRenderer.data_me(%{tag | alters: []})
                    }
                  )
                end)

                {:ok, tag}

              _ ->
                {:error, :changeset}
            end
        end
    end
  end

  def attach_alter_to_tag(system_identity, tag_id, alter_identity) do
    case Alters.resolve_alter(system_identity, alter_identity) do
      false ->
        {:error, :alter_not_found}

      alter_id ->
        system_id = Accounts.id_from_system_identity(system_identity, :system)

        %AlterTag{
          user_id: system_id,
          tag_id: tag_id,
          alter_id: alter_id
        }
        |> AlterTag.changeset()
        |> Repo.insert()
        |> case do
          {:ok, _} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "tag_updated",
                %{tag: TagRenderer.data_me(get_tag({:system, system_id}, tag_id))}
              )
            end)

            :ok

          _ ->
            {:error, :changeset}
        end
    end
  end

  def detach_alter_from_tag(system_identity, tag_id, alter_identity) do
    where = unwrap_system_identity_where(system_identity, tag_id: tag_id)

    case Alters.resolve_alter(system_identity, alter_identity) do
      false ->
        {:error, :alter_not_found}

      alter_id ->
        query =
          AlterTag
          |> where(^where)
          |> where(alter_id: ^alter_id)

        case Repo.delete_all(query) do
          {1, _} ->
            spawn(fn ->
              system_id = Accounts.id_from_system_identity(system_identity, :system)

              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "tag_updated",
                %{tag: TagRenderer.data_me(get_tag({:system, system_id}, tag_id))}
              )
            end)

            :ok

          _ ->
            {:error, :not_found}
        end
    end
  end

  def set_parent_tag(system_identity, tag_id, parent_tag_id) do
    tag = Task.async(fn -> get_tag(system_identity, tag_id) end)
    parent = Task.async(fn -> get_tag(system_identity, parent_tag_id) end)

    case {Task.await(tag), Task.await(parent)} do
      {tag, parent} when tag == nil or parent == nil ->
        {:error, :not_found}

      {tag, parent} when tag.user_id != parent.user_id ->
        {:error, :not_found}

      {tag, _parent} ->
        tag
        |> Tag.changeset(%{parent_tag_id: parent_tag_id})
        |> Repo.update()
        |> case do
          {:ok, _} ->
            spawn(fn ->
              system_id = Accounts.id_from_system_identity(system_identity, :system)

              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "tag_updated",
                %{tag: TagRenderer.data_me(get_tag({:system, system_id}, tag_id))}
              )
            end)

            :ok

          _ ->
            {:error, :changeset}
        end
    end
  end

  def remove_parent_tag(system_identity, tag_id) do
    case get_tag(system_identity, tag_id) do
      nil ->
        {:error, :not_found}

      tag ->
        tag
        |> Tag.changeset(%{parent_tag_id: nil})
        |> Repo.update()
        |> case do
          {:ok, _} ->
            spawn(fn ->
              system_id = Accounts.id_from_system_identity(system_identity, :system)

              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "tag_updated",
                %{tag: TagRenderer.data_me(get_tag({:system, system_id}, tag_id))}
              )
            end)

            :ok

          _ ->
            {:error, :changeset}
        end
    end
  end

  def update_tag(system_identity, tag_id, attrs) do
    case get_tag(system_identity, tag_id) do
      nil ->
        {:error, :not_found}

      tag ->
        result =
          tag
          |> Tag.changeset(Map.drop(attrs, [:parent_tag_id]))
          |> Repo.update()

        case result do
          {:ok, bare_tag} ->
            system_id = bare_tag.user_id

            tag = get_tag({:system, system_id}, tag_id)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{bare_tag.user_id}",
                "tag_updated",
                %{tag: TagRenderer.data_me(tag)}
              )
            end)

            {:ok, tag}

          _ ->
            {:error, :changeset}
        end
    end
  end

  def delete_tag(system_identity, tag_id) do
    where = unwrap_system_identity_where(system_identity, id: tag_id)

    query =
      Tag
      |> where(^where)

    case Repo.delete_all(query) do
      {1, _} ->
        spawn(fn ->
          system_id = Accounts.id_from_system_identity(system_identity, :system)

          OctoconWeb.Endpoint.broadcast!(
            "system:#{system_id}",
            "tag_deleted",
            %{tag_id: tag_id}
          )
        end)

        :ok

      _ ->
        {:error, :not_found}
    end
  end
end
