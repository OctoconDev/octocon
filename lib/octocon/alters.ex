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

  alias Ecto.Multi

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

  @bare_fields [:id, :name, :avatar_url, :pronouns, :color, :security_level]

  defp unwrap_system_identity_where(system_identity, extra \\ []) do
    case system_identity do
      {:system, system_id} ->
        [user_id: system_id] |> Keyword.merge(extra)

      {:discord, _} = identity ->
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

    Repo.exists?(query)
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

    case Repo.one(query) do
      nil -> false
      id -> id
    end
  end

  @doc """
  Returns the total number of alters in the database.
  """
  def count do
    Repo.aggregate(Alter, :count)
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

    case Repo.one(query) do
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

    query =
      Alter
      |> where(^where)
      |> select([a], struct(a, ^fields))
      |> order_by([a], asc: a.id)

    Repo.all(query)
  end

  @doc """
  Returns all alters with the given IDs associated with the given system identity. If no `fields` are
  provided, all struct fields are returned.

  `alter_ids` MUST be a list of alter IDs (i.e. integers), NOT alter identities.

  Provide a `fields` list to only return the specified fields to save on bandwidth.
  """
  def get_alters_by_id_bounded(system_identity, alter_ids, fields \\ @all_fields) do
    where = unwrap_system_identity_where(system_identity)

    query =
      Alter
      |> where(^where)
      |> where([a], a.id in ^alter_ids)
      |> select([a], struct(a, ^fields))
      |> order_by([a], asc: a.id)

    Repo.all(query)
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
        {:error, :no_alter} -> :private
      end

    if can_view_entity?(friendship_level, security_level) and alter != {:error, :no_alter} do
      {:ok, alter} = alter
      fields = get_guarded_fields(user_fields, alter.fields, friendship_level)
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
      fields = get_guarded_fields(user_fields, alter.fields, friendship_level)
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
  def create_alter_internal(user, attrs) do
    new_id = user.lifetime_alter_count + 1

    transaction =
      Multi.new()
      # Increment the user's alter count
      |> Multi.update(:user, Accounts.change_user(user, %{lifetime_alter_count: new_id}))
      # Create the alter
      |> Multi.insert(:alter, change_alter(%Alter{user_id: user.id, id: new_id}, attrs))
      |> Repo.transaction()

    case transaction do
      {:ok, %{alter: alter}} ->
        spawn(fn ->
          OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "alter_created", %{
            alter: alter |> OctoconWeb.System.AlterJSON.data_me()
          })
        end)

        {:ok, new_id, alter}

      {:error, _, _, _} ->
        {:error, :database}
    end
  end

  @doc """
  Creates a new alter given a system identity and a map of `attrs`.

  **PROXIED**: If this function is executed on an **auxiliary** node, it will be proxied to a random **primary** node.
  """
  def create_alter(system_identity, attrs \\ %{}) do
    case Accounts.get_user(system_identity) do
      nil ->
        {:error, :no_user}

      user ->
        Fly.Postgres.rpc_and_wait(__MODULE__, :create_alter_internal, [user, attrs])
        # create_alter_internal(user, attrs)
    end
  end

  @doc """
  Creates a new alter given a system identity and a map of `attrs`.

  **PROXIED**: If this function is executed on an **auxiliary** node, it will be proxied to a random **primary** node.

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

  @doc false
  def delete_alter_internal(system_identity, alter_identity) do
    alter_id = resolve_alter(system_identity, alter_identity)
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    if alter_id != false do
      transaction =
        Repo.transaction(fn repo ->
          where =
            unwrap_system_identity_where(
              system_identity,
              unwrap_alter_identity_where(alter_identity)
            )

          query =
            Alter
            |> where(^where)

          repo.delete_all(query)
        end)

      case transaction do
        {:ok, _} ->
          spawn(fn ->
            OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "alter_deleted", %{
              alter_id: alter_id
            })
          end)

          spawn(fn ->
            Octocon.Utils.nuke_existing_avatars!(system_id, alter_id)
          end)

          :ok

        {:error, _} ->
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
  Deletes an alter given a system identity and an alter identity.

  **PROXIED**: If this function is executed on an **auxiliary** node, it will be proxied to a random **primary** node.

  Raises an error if the alter is not found.
  """
  def delete_alter(system_identity, alter_identity) do
    Fly.Postgres.rpc_and_wait(__MODULE__, :delete_alter_internal, [
      system_identity,
      alter_identity
    ])
  end

  def update_alter_internal(system_identity, alter_identity, attrs) do
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

      changeset =
        if fields == nil do
          change_alter(base_struct, attrs)
        else
          change_alter(base_struct, Map.drop(attrs, [:fields]))
          |> Ecto.Changeset.put_embed(:fields, fields)
        end

      if changeset.valid? do
        attrs! =
          if fields == nil do
            attrs
          else
            Map.put(attrs, :fields, fields)
          end

        where =
          unwrap_system_identity_where(
            system_identity,
            unwrap_alter_identity_where(alter_identity)
          )

        query =
          Alter
          |> where(^where)
          # NOTE: Check
          |> update(set: ^Keyword.new(attrs!))

        case Repo.update_all(query, []) do
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
  rescue
    e in Postgrex.Error ->
      case e.postgres.constraint do
        "alters_user_id_alias_index" ->
          {:error, :alias_taken}

        _ ->
          {:error, :database}
      end
  end

  @doc """
  Updates an alter given a system identity, an alter identity, and a map of `attrs`.

  **PROXIED**: If this function is executed on an **auxiliary** node, it will be proxied to a random **primary** node.
  """
  def update_alter(system_identity, alter_identity, attrs) do
    Fly.Postgres.rpc_and_wait(__MODULE__, :update_alter_internal, [
      system_identity,
      alter_identity,
      attrs
    ])
  end

  @doc """
  Updates an alter given a system identity, an alter identity, and a map of `attrs`.

  **PROXIED**: If this function is executed on an **auxiliary** node, it will be proxied to a random **primary** node.

  Raises an error if the alter is not found.
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
