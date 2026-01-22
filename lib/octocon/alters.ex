defmodule Octocon.Alters do
  @moduledoc """
  The Alters context.

  This module represents the data layer for working with alters. Almost all operations
  require an alter "identity", which is a tuple of the form:

  - `{:id, alter_id}`: References an alter by their alter ID (integer up to 32,767).
  - `{:alias, alter_alias}`: References an alter by their unique alias.

  Additionally, most operations require a system identity. See `Octocon.Accounts` for more
  information on system identities.
  """

  import Ecto.Query, warn: false

  alias Octocon.{
    Accounts,
    Alters.Alter,
    Friendships,
    Repo
  }

  alias Octocon.Alters.Field, as: AlterField

  @all_fields Alter.__struct__()
              |> Map.from_struct()
              |> Map.drop(Alter.dropped_fields())
              |> Map.keys()

  @bare_fields [:id, :name, :avatar_url, :pronouns, :color, :security_level, :alias, :pinned]

  def bare_fields, do: @bare_fields

  defp unwrap_system_identity_where(system_identity, extra \\ []) do
    case system_identity do
      {:system, system_id} ->
        [user_id: system_id] |> Keyword.merge(extra)

      {_, _} = identity ->
        [user_id: Accounts.id_from_system_identity(identity, :system)]
        |> Keyword.merge(extra)
    end
  end

  defp unwrap_alter_identity_where(alter_identity) do
    case alter_identity do
      {:id, alter_id} -> [id: alter_id]
      {:alias, aliaz} when aliaz != nil -> [alias: aliaz]
    end
  end

  @doc """
  Checks if an alter alias is already taken.
  """
  def alias_taken?(system_identity, aliaz) do
    where = unwrap_system_identity_where(system_identity, alias: aliaz)

    query =
      Alter
      |> where(^where)

    case Repo.all_regional(query, {:user, system_identity}) do
      [] -> false
      _ -> true
    end
  end

  @doc """
  Resolves an alter identity to an alter ID.
  """
  def resolve_alter(system_identity, alter_identity)

  def resolve_alter(nil, _), do: false
  def resolve_alter(_, nil), do: false

  def resolve_alter(system_identity, alter_identity) do
    where =
      unwrap_system_identity_where(system_identity, unwrap_alter_identity_where(alter_identity))

    query =
      Alter
      |> where(^where)
      |> select([a], a.id)

    case Repo.one_regional(query, {:user, system_identity}) do
      nil -> false
      id -> id
    end
  end

  def resolve_alter_id_dumb(system_identity, {:id, alter_id}), do: alter_id

  def resolve_alter_id_dumb(system_identity, {:alias, aliaz}) do
    where = unwrap_system_identity_where(system_identity, alias: aliaz)

    query =
      Alter
      |> where(^where)
      |> select([a], a.id)

    Repo.one_regional(query, {:user, system_identity})
  end

  @doc """
  Returns the total number of alters in the database.
  """
  def count do
    Repo.region_list()
    |> Enum.map(&Repo.aggregate(Alter, :count, prefix: &1))
    |> Enum.sum()
  end

  @doc """
  Gets an alter by their identity. If no `fields` are provided, all struct fields are returned.

  Provide a `fields` list to only return the specified fields to save on bandwidth.
  """
  def get_alter_by_id(system_identity, alter_identity, fields \\ @all_fields) do
    where =
      unwrap_system_identity_where(system_identity, unwrap_alter_identity_where(alter_identity))

    query =
      Alter
      |> where(^where)
      |> select([a], struct(a, ^fields))

    case Repo.one_regional(query, {:user, system_identity}) do
      nil ->
        case alter_identity do
          {:id, _} -> {:error, :no_alter_id}
          {:alias, _} -> {:error, :no_alter_alias}
        end

      alter ->
        {:ok, alter}
    end
  end

  @doc """
  Gets an alter by their identity. If no `fields` are provided, all struct fields are returned.

  Provide a `fields` list to only return the specified fields to save on bandwidth.

  Raises an error if the alter is not found.
  """
  def get_alter_by_id!(system_identity, alter_identity, fields \\ @all_fields) do
    case get_alter_by_id(system_identity, alter_identity, fields) do
      {:ok, alter} -> alter
      {:error, :no_alter_id} -> raise "Alter not found with ID"
      {:error, :no_alter_alias} -> raise "Alter not found with alias"
    end
  end

  @doc """
  Returns all alters associated with the given system identity. If no `fields` are provided,
  all struct fields are returned.

  Provide a `fields` list to only return the specified fields to save on bandwidth.
  """
  def get_alters_by_id(system_identity, fields \\ @all_fields) do
    where = unwrap_system_identity_where(system_identity)

    Alter
    |> where(^where)
    |> select([a], struct(a, ^fields))
    |> Repo.all_regional({:user, system_identity})
    |> Enum.sort_by(& &1.id, :asc)
  end

  @doc """
  Returns all alters with the given IDs associated with the given system identity. If no `fields` are
  provided, all struct fields are returned.

  `alter_ids` MUST be a list of alter IDs (i.e. integers), NOT alter identities.

  Provide a `fields` list to only return the specified fields to save on bandwidth.
  """
  def get_alters_by_id_bounded(system_identity, alter_ids, fields \\ @all_fields) do
    where = unwrap_system_identity_where(system_identity)

    Alter
    |> where(^where)
    |> where([a], a.id in ^alter_ids)
    |> select([a], struct(a, ^fields))
    |> Repo.all_regional({:user, system_identity})
    |> Enum.sort_by(& &1.id, :asc)
  end

  @doc """
  Gets an alter by their identity.

  This function is guarded by the caller's friendship level with the system. For example, an alter with
  a security level of `:trusted_only` can only be viewed by a caller with a friendship level of `:trusted_friend`.
  """
  def get_alter_guarded(system_identity, alter_identity, caller_identity) do
    friendship_level = Friendships.get_friendship_level(system_identity, caller_identity)
    user_fields = Accounts.get_user_fields(system_identity)

    alter =
      get_alter_by_id(system_identity, alter_identity, [
        :security_level,
        :name,
        :pronouns,
        :description,
        :color,
        :fields,
        :avatar_url,
        :discord_proxies,
        :id
      ])

    # Pretend that the alter doesn't exist if the caller is not friends with the system
    security_level =
      case alter do
        {:ok, alter} -> alter.security_level
        {:error, :no_alter_id} -> :private
        {:error, :no_alter_alias} -> :private
      end

    if can_view_entity?(friendship_level, security_level) and alter != {:error, :no_alter_id} and
         alter != {:error, :no_alter_alias} do
      {:ok, alter} = alter
      fields = get_guarded_fields(user_fields, alter.fields || [], friendship_level)
      {:ok, %{alter | fields: fields}}
    else
      :error
    end
  end

  @doc """
  Gets all alters associated with the given system identity.

  This function is guarded by the caller's friendship level with the system. For example, an alter with
  a security level of `:trusted_only` can only be viewed by a caller with a friendship level of `:trusted_friend`.
  """
  def get_alters_guarded(system_identity, caller_identity) do
    friendship_level = Friendships.get_friendship_level(system_identity, caller_identity)
    user_fields = Accounts.get_user_fields(system_identity)

    get_alters_by_id(system_identity)
    |> Enum.filter(&can_view_entity?(friendship_level, &1.security_level))
    |> Enum.map(fn alter ->
      fields = get_guarded_fields(user_fields, alter.fields || [], friendship_level)
      %{alter | fields: fields}
    end)
  end

  @doc false
  def get_alters_guarded_bare_batch(system_identity, friendship_level, alter_ids) do
    get_alters_by_id_bounded(system_identity, alter_ids, @bare_fields)
    |> Stream.filter(&can_view_entity?(friendship_level, &1.security_level))
    |> Enum.map(&Map.drop(&1, [:security_level]))
  end

  defp get_guarded_fields(user_fields, alter_fields, friendship_level) do
    user_fields
    |> Stream.filter(fn user_field ->
      can_view_entity?(friendship_level, user_field.security_level)
    end)
    |> Stream.filter(fn user_field ->
      Enum.any?(alter_fields, &(&1.id == user_field.id))
    end)
    |> Enum.map(fn user_field ->
      alter_field = Enum.find(alter_fields, &(&1.id == user_field.id))

      %{
        id: user_field.id,
        name: user_field.name,
        type: user_field.type,
        value: alter_field.value
      }
    end)
  end

  def get_random_alter(system_identity, fields \\ @all_fields) do
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    all_alter_ids =
      from(
        a in Alter,
        where: a.user_id == ^system_id,
        select: a.id
      )
      |> Repo.all_regional({:user, system_identity})

    if all_alter_ids == [] do
      nil
    else
      random_alter_id = Enum.random(all_alter_ids)

      where =
        unwrap_system_identity_where(system_identity, id: random_alter_id)

      query =
        Alter
        |> where(^where)
        |> select([a], struct(a, ^fields))

      case Repo.one_regional(query, {:user, system_identity}) do
        nil -> nil
        alter -> {:ok, alter}
      end
    end
  end

  @doc """
  Given the friendship level and security level of an entity, returns whether the entity can be viewed.

  - A security level of `:public` can be viewed by anyone.
  - A security level of `:friends_only` can be viewed by a friendship level of `:friend` or `:trusted_friend`.
  - A security level of `:trusted_only` can be viewed by a friendship level of `:trusted_friend`.
  - A security level of `:private` can never be viewed externally.
  """
  def can_view_entity?(friendship_level, security_level)

  def can_view_entity?(_, :public), do: true

  def can_view_entity?(:friend, :friends_only), do: true

  def can_view_entity?(:trusted_friend, target) when target in [:friends_only, :trusted_only],
    do: true

  def can_view_entity?(_, _), do: false

  @doc false
  def create_alter_internal(user, attrs, force_id \\ nil) do
    alter_id = if force_id == nil, do: user.lifetime_alter_count + 1, else: force_id

    case Accounts.update_user(user, %{lifetime_alter_count: alter_id}) do
      {:ok, _user} ->
        {:ok, alter} =
          change_alter(%Alter{user_id: user.id, id: alter_id}, attrs)
          |> Repo.insert_regional({:user, {:system, user.id}})

        spawn(fn ->
          OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "alter_created", %{
            alter: alter |> OctoconWeb.System.AlterJSON.data_me()
          })
        end)

        {:ok, alter_id, get_alter_by_id!({:system, user.id}, {:id, alter_id})}

      {:error, _changeset} ->
        {:error, :database}
    end
  rescue
    _ ->
      if force_id == nil do
        # Manually resync the user's alter count
        highest_alter_id = get_highest_alter_id({:system, user.id})
        create_alter_internal(user, attrs, highest_alter_id + 1)
      end
  end

  def get_highest_alter_id(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      Alter
      |> where(^where)
      |> select([a], a.id)
      |> order_by([a], desc: a.id)
      |> limit(1)

    case Repo.one_regional(query, {:user, system_identity}) do
      nil -> 0
      id -> id
    end
  end

  def create_alter(system_identity, attrs \\ %{}, force_id \\ nil) do
    case Accounts.get_user(system_identity) do
      nil ->
        {:error, :no_user}

      user ->
        alter_id = if force_id == nil, do: user.lifetime_alter_count + 1, else: force_id

        case Accounts.update_user(user, %{lifetime_alter_count: alter_id}) do
          {:ok, _user} ->
            {:ok, alter} =
              change_alter(%Alter{user_id: user.id, id: alter_id}, attrs)
              |> Repo.insert_regional({:user, {:system, user.id}})

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "alter_created", %{
                alter: alter |> OctoconWeb.System.AlterJSON.data_me()
              })
            end)

            {:ok, alter_id, get_alter_by_id!({:system, user.id}, {:id, alter_id})}

          {:error, _changeset} ->
            {:error, :database}
        end
    end
  end

  @doc """
  Creates a new alter given a system identity and a map of `attrs`.

  Raises an error if a user is not found with the given `system_identity`.
  """
  def create_alter!(system_identity, attrs \\ %{}) do
    case create_alter(system_identity, attrs) do
      {:ok, _, alter} ->
        alter

      {:error, :no_user} ->
        raise RuntimeError, "User not found with system_identity #{inspect(system_identity)}"

      {:error, :database} ->
        raise RuntimeError, "Database error"
    end
  end

  @doc """
  Deletes an alter given a system identity and an alter identity.
  """
  def delete_alter(system_identity, alter_identity) do
    alter_id = resolve_alter(system_identity, alter_identity)
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    if alter_id != false do
      where =
        unwrap_system_identity_where(system_identity, unwrap_alter_identity_where(alter_identity))

      query =
        Alter
        |> where(^where)

      case Repo.delete_all_regional(query, {:user, system_identity}) do
        {1, _} ->
          spawn(fn ->
            system_identity = {:system, system_id}
            Octocon.Journals.delete_alter_journal_entries(system_identity, alter_id)
            Octocon.Tags.delete_alter_tags(system_identity, alter_id)
            Octocon.Fronts.delete_alter_fronts(system_identity, alter_id)
          end)

          spawn(fn ->
            OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "alter_deleted", %{
              alter_id: alter_id
            })
          end)

          spawn(fn ->
            Octocon.Utils.nuke_existing_avatars!(system_id, alter_id)
          end)

          :ok

        _ ->
          {:error, :database}
      end
    else
      case alter_identity do
        {:id, _} -> {:error, :no_alter_id}
        {:alias, _} -> {:error, :no_alter_alias}
      end
    end
  end

  @doc """
  Updates an alter given a system identity, an alter identity, and a map of `attrs`.
  """
  def update_alter(system_identity, alter_identity, attrs) do
    alter_id = resolve_alter(system_identity, alter_identity)
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    if alter_id != false do
      base_struct = %Alter{user_id: system_id, id: alter_id}

      fields =
        if attrs[:fields] == nil do
          nil
        else
          attrs[:fields]
          |> Enum.map(fn field ->
            %AlterField{
              id: field["id"],
              value: field["value"]
            }
          end)
        end

      attrs =
        if fields == nil do
          attrs
        else
          Map.put(attrs, :fields, fields)
        end

      changeset = change_alter(base_struct, attrs)

      if changeset.valid? do
        alter_id = resolve_alter_id_dumb(system_identity, alter_identity)

        query =
          Alter
          |> where([a], a.id == ^alter_id and a.user_id == ^system_id)
          |> update(set: ^Keyword.new(attrs))

        case Repo.update_all_regional(query, [], {:user, system_identity}) do
          {0, _} ->
            {:error, :no_alter}

          {1, _} ->
            spawn(fn ->
              alter = get_alter_by_id!(system_identity, {:id, alter_id})

              OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "alter_updated", %{
                alter:
                  alter
                  |> OctoconWeb.System.AlterJSON.data_me()
              })
            end)

            :ok

          _ ->
            {:error, :database}
        end
      else
        {:error, :changeset}
      end
    else
      case alter_identity do
        {:id, _} -> {:error, :no_alter_id}
        {:alias, _} -> {:error, :no_alter_alias}
      end
    end
  end

  @doc """
  Updates an alter given a system identity, an alter identity, and a map of `attrs`.
  """
  def update_alter!(system_identity, alter_identity, attrs) do
    case update_alter(system_identity, alter_identity, attrs) do
      :ok ->
        :ok

      {:error, :no_alter_id} ->
        raise "Alter not found with ID"

      {:error, :no_alter_alias} ->
        raise "Alter not found with alias"

      {:error, :database} ->
        raise "Database error"

      {:error, :changeset} ->
        raise "Invalid changeset"
    end
  end

  @doc """
  Builds a changeset based on the given `Octocon.Alters.Alter` struct and `attrs` to change.
  """
  def change_alter(%Alter{} = alter, attrs \\ %{}) do
    Alter.changeset(alter, attrs)
  end
end
